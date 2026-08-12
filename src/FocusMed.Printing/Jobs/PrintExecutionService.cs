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

        // ── Step 2: booklet stapling (SaddleStitch = center fold + 2 staples) ──
        if (request.Profile.IsBooklet)
        {
            // Log every stapling option the driver exposes so we know what it really supports
            try
            {
                var caps = queue.GetPrintCapabilities(delta);
                var supported = caps.StaplingCapability.Select(s => s.ToString()).ToList();
                logger.LogInformation("Driver stapling capabilities: [{Options}]", string.Join(", ", supported));

                if (caps.StaplingCapability.Any(s => s == Stapling.SaddleStitch))
                {
                    delta.Stapling = Stapling.SaddleStitch;
                    logger.LogInformation("✓ SaddleStitch (fold + centre staple) set via Windows Print Schema");
                }
                else
                {
                    // Driver doesn't advertise SaddleStitch in standard schema.
                    // Set it anyway — some Konica PCL drivers accept it even if not listed.
                    delta.Stapling = Stapling.SaddleStitch;
                    logger.LogWarning("SaddleStitch NOT in driver capabilities list — forcing it anyway. " +
                        "If the finisher ignores it, the driver needs private-namespace XML. Supported: [{Options}]",
                        string.Join(", ", supported));
                }
            }
            catch (Exception ex)
            {
                // GetPrintCapabilities can throw on some drivers; fall back to direct assignment
                logger.LogWarning(ex, "GetPrintCapabilities failed — setting SaddleStitch without capability check");
                delta.Stapling = Stapling.SaddleStitch;
            }
        }
        else
        {
            delta.Stapling = Stapling.None;
        }

        // ── Step 3: MergeAndValidatePrintTicket ─────────────────────────────────
        // CRITICAL for PCL drivers: without this call the ticket is never committed
        // to driver-level DevMode and finisher settings are silently ignored.
        PrintTicket finalTicket;
        try
        {
            var merged = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, delta);
            // System.Printing.ValidationResult: .ValidatedPrintTicket = the merged ticket,
            //                                   .ValidationStatus      = Valid/Conflict enum
            finalTicket = merged.ValidatedPrintTicket;

            logger.LogInformation(
                "Ticket merge/validate: conflict={Status} | Orient={Orient} | Duplex={Duplex} | Staple={Staple} | Size={Size}",
                merged.ConflictStatus,
                finalTicket.PageOrientation,
                finalTicket.Duplexing,
                finalTicket.Stapling,
                finalTicket.PageMediaSize?.PageMediaSizeName);

            // Detect if the driver stripped our SaddleStitch (conflict resolution)
            if (request.Profile.IsBooklet && finalTicket.Stapling != Stapling.SaddleStitch)
            {
                logger.LogWarning(
                    "Driver overrode SaddleStitch -> {Actual}. The finisher may need to be configured " +
                    "through the driver UI or a private-namespace PrintTicket fragment.", finalTicket.Stapling);
            }
        }
        catch (Exception ex)
        {
            // MergeAndValidate can fail on some host systems (no XPS/spool service access).
            // Fall back to the unvalidated ticket — better than nothing.
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
