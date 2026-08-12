using System.Drawing;
using System.Drawing.Imaging;
using System.Printing;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using FocusMed.Printing.Imposition;
using FocusMed.Printing.Profiles;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace FocusMed.Printing.Jobs;

internal sealed class PrintExecutionService(
    IBookletImpositionService bookletService,
    ILogger<PrintExecutionService> logger) : IPrintExecutionService
{
    public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken ct = default)
    {
        logger.LogInformation("Print: '{Pdf}' -> '{Printer}' profile='{Profile}' copies={Copies}",
            request.PdfPath, request.PrinterName, request.Profile.Name, request.Copies);

        string pdfPath = request.PdfPath;

        if (request.Profile.IsBooklet)
        {
            try
            {
                pdfPath = await bookletService.ComposeBookletAsync(request.PdfPath, request.Profile.PaperSizeName ?? "A3", ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Booklet imposition failed");
                return new PrintJobResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        try
        {
            return RunInStaThread(() => PrintViaXps(pdfPath, request));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Print failed");
            return new PrintJobResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            if (pdfPath != request.PdfPath && File.Exists(pdfPath))
                try { File.Delete(pdfPath); } catch { }
        }
    }

    private PrintJobResult PrintViaXps(string pdfPath, PrintJobRequest request)
    {
        // Read imposed PDF to get exact page dimensions
        double pageWpt, pageHpt;
        int pageCount;
        using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
        {
            pageCount = doc.Pages.Count;
            pageWpt = doc.Pages[0].Width;
            pageHpt = doc.Pages[0].Height;
        }

        // Convert to WPF units (1pt = 96/72 = 1.333 WPF)
        double wpfW = pageWpt * 96.0 / 72.0;
        double wpfH = pageHpt * 96.0 / 72.0;
        double mmW = pageWpt * 25.4 / 72.0;
        double mmH = pageHpt * 25.4 / 72.0;

        bool isLandscape = mmW > mmH;
        bool isA4 = request.Profile.PaperSizeName?.Contains("A4", StringComparison.OrdinalIgnoreCase) == true;

        logger.LogInformation("Page: {W}x{H}mm -> XPS {X}x{Y}", mmW, mmH, wpfW, wpfH);

        // Setup print queue
        using var printServer = new LocalPrintServer();
        using var queue = printServer.GetPrintQueue(request.PrinterName);

        // ── Step 1: build our desired delta ticket ──────────────────────────────
        // Start from the driver's own default so vendor-specific fields are intact
        var delta = queue.DefaultPrintTicket;

        delta.PageMediaSize = isA4
            ? new PageMediaSize(PageMediaSizeName.ISOA4)
            : new PageMediaSize(PageMediaSizeName.ISOA3);

        delta.PageOrientation = isLandscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        delta.InputBin        = InputBin.AutoSelect;
        delta.CopyCount       = request.Copies;

        if (request.Profile.RequiresDuplex)
            delta.Duplexing = request.Profile.UseDuplexShortEdge
                ? Duplexing.TwoSidedShortEdge   // A3/A4 Booklet: fold on short axis
                : Duplexing.TwoSidedLongEdge;
        else
            delta.Duplexing = Duplexing.OneSided;

        // ── Step 2: booklet stapling & folding via PrintSchema & Driver XML ──
        if (request.Profile.IsBooklet)
        {
            try
            {
                // Set standard PrintSchema properties first
                delta.Stapling = Stapling.SaddleStitch;

                // Inspect driver XML capabilities to discover vendor-specific Fold & Staple feature names
                using var capsStream = queue.GetPrintCapabilitiesAsXml(delta);
                var capsDoc = new System.Xml.XmlDocument();
                capsDoc.Load(capsStream);

                var nsmgr = new System.Xml.XmlNamespaceManager(capsDoc.NameTable);
                nsmgr.AddNamespace("psf", "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework");
                nsmgr.AddNamespace("psk", "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords");

                var featureNodes = capsDoc.SelectNodes("//psf:Feature", nsmgr);
                var discoveredFeatures = new List<string>();

                if (featureNodes != null)
                {
                    foreach (System.Xml.XmlNode feature in featureNodes)
                    {
                        string featName = feature.Attributes?["name"]?.Value ?? "";
                        if (featName.Contains("Staple", StringComparison.OrdinalIgnoreCase) ||
                            featName.Contains("Fold", StringComparison.OrdinalIgnoreCase) ||
                            featName.Contains("Booklet", StringComparison.OrdinalIgnoreCase) ||
                            featName.Contains("Bind", StringComparison.OrdinalIgnoreCase) ||
                            featName.Contains("Finish", StringComparison.OrdinalIgnoreCase))
                        {
                            var optionNames = new List<string>();
                            var options = feature.SelectNodes("psf:Option", nsmgr);
                            if (options != null)
                            {
                                foreach (System.Xml.XmlNode opt in options)
                                {
                                    optionNames.Add(opt.Attributes?["name"]?.Value ?? "");
                                }
                            }
                            discoveredFeatures.Add($"{featName} => [{string.Join(", ", optionNames)}]");
                        }
                    }
                }

                logger.LogInformation("Discovered finisher features for '{Printer}':\n{Features}",
                    request.PrinterName, string.Join("\n", discoveredFeatures));

                // Now modify delta PrintTicket XML to inject fold/staple/booklet options if missing
                delta = InjectBookletXml(delta, capsDoc, nsmgr, logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to scan PrintCapabilities XML or inject booklet XML");
                delta.Stapling = Stapling.SaddleStitch;
            }
        }
        else
        {
            delta.Stapling = Stapling.None;
        }

        // ── Step 3: MergeAndValidatePrintTicket ─────────────────────────────────
        PrintTicket finalTicket;
        try
        {
            var merged = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, delta);
            finalTicket = merged.ValidatedPrintTicket;

            logger.LogInformation(
                "Ticket merge/validate: conflict={Status} | Orient={Orient} | Duplex={Duplex} | Staple={Staple} | Size={Size}",
                merged.ConflictStatus,
                finalTicket.PageOrientation,
                finalTicket.Duplexing,
                finalTicket.Stapling,
                finalTicket.PageMediaSize?.PageMediaSizeName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MergeAndValidatePrintTicket failed — using unvalidated delta ticket");
            finalTicket = delta;
        }

        // ── Step 4: build XPS document ──────────────────────────────────────────
        byte[] pdfBytes = File.ReadAllBytes(pdfPath);
        var fixedDoc = new FixedDocument();

        for (int i = 0; i < pageCount; i++)
        {
            using var ms = new MemoryStream(pdfBytes, writable: false);
            using var skBitmap = Conversion.ToImage(ms, page: i, options: new RenderOptions
            {
                Dpi = 300,
                Grayscale = request.ForceGrayscale,
                BackgroundColor = SKColors.White
            });

            using var pngStream = new MemoryStream();
            skBitmap.Encode(pngStream, SKEncodedImageFormat.Png, 100);
            pngStream.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = pngStream;
            bmp.EndInit();
            bmp.Freeze();

            var page = new FixedPage { Width = wpfW, Height = wpfH };
            page.Children.Add(new System.Windows.Controls.Image
            {
                Source = bmp,
                Width  = wpfW,
                Height = wpfH,
                Stretch = System.Windows.Media.Stretch.Uniform
            });

            var pc = new PageContent();
            ((System.Windows.Markup.IAddChild)pc).AddChild(page);
            fixedDoc.Pages.Add(pc);
        }

        // ── Step 5: send to printer using the validated ticket ───────────────────
        PrintQueue.CreateXpsDocumentWriter(queue).Write(fixedDoc, finalTicket);

        logger.LogInformation("Sent: {Pages} pages {W}x{H}mm -> '{Printer}'",
            pageCount, mmW, mmH, request.PrinterName);

        string resolvedPaperSize = request.Profile.PaperSizeName ?? (isLandscape ? "A3" : "A4");

        return new PrintJobResult
        {
            Success          = true,
            PaperSizeUsed    = resolvedPaperSize,
            Landscape        = isLandscape,
            Duplex           = request.Profile.RequiresDuplex,
            PagesPrinted     = pageCount,
            ImposedPdfPath   = request.Profile.IsBooklet ? pdfPath : null,
            DetectedBookletPaper = request.Profile.IsBooklet ? resolvedPaperSize : null
        };
    }


    private static PrintTicket InjectBookletXml(PrintTicket ticket, System.Xml.XmlDocument capsDoc, System.Xml.XmlNamespaceManager capsNsmgr, ILogger logger)
    {
        try
        {
            using var stream = ticket.GetXmlStream();
            var doc = new System.Xml.XmlDocument();
            doc.Load(stream);

            var nsmgr = new System.Xml.XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("psf", "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework");
            nsmgr.AddNamespace("psk", "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords");

            // Look through capsDoc for any Feature related to Fold/Staple/Booklet
            var featureNodes = capsDoc.SelectNodes("//psf:Feature", capsNsmgr);
            if (featureNodes != null)
            {
                foreach (System.Xml.XmlNode featureNode in featureNodes)
                {
                    string featName = featureNode.Attributes?["name"]?.Value ?? "";
                    if (!featName.Contains("Staple", StringComparison.OrdinalIgnoreCase) &&
                        !featName.Contains("Fold", StringComparison.OrdinalIgnoreCase) &&
                        !featName.Contains("Booklet", StringComparison.OrdinalIgnoreCase) &&
                        !featName.Contains("Bind", StringComparison.OrdinalIgnoreCase) &&
                        !featName.Contains("Finish", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Find options matching booklet / fold / staple
                    var options = featureNode.SelectNodes("psf:Option", capsNsmgr);
                    if (options == null) continue;

                    string? selectedOption = null;
                    foreach (System.Xml.XmlNode opt in options)
                    {
                        string optName = opt.Attributes?["name"]?.Value ?? "";
                        if (optName.Contains("Saddle", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("Booklet", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("CenterFold", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("HalfFold", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("Fold", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("CenterStaple", StringComparison.OrdinalIgnoreCase) ||
                            optName.Contains("2Position", StringComparison.OrdinalIgnoreCase))
                        {
                            selectedOption = optName;
                            break;
                        }
                    }

                    if (selectedOption != null)
                    {
                        logger.LogInformation("Injecting XML feature '{Feature}' = '{Option}' into PrintTicket", featName, selectedOption);

                        // Remove existing Feature node if present in ticket doc
                        var existing = doc.SelectSingleNode($"//psf:Feature[@name='{featName}']", nsmgr);
                        if (existing != null)
                        {
                            existing.ParentNode?.RemoveChild(existing);
                        }

                        // Create new Feature element
                        var featElem = doc.CreateElement("psf", "Feature", "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework");
                        featElem.SetAttribute("name", featName);

                        var optElem = doc.CreateElement("psf", "Option", "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework");
                        optElem.SetAttribute("name", selectedOption);
                        featElem.AppendChild(optElem);

                        doc.DocumentElement?.AppendChild(featElem);
                    }
                }
            }

            using var outStream = new MemoryStream();
            doc.Save(outStream);
            outStream.Position = 0;
            return new PrintTicket(outStream);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to inject XML booklet features into PrintTicket");
            return ticket;
        }
    }

    private static T RunInStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? ex = null;
        var t = new Thread(() => { try { result = action(); } catch (Exception e) { ex = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (ex != null) throw ex;
        return result;
    }
}
