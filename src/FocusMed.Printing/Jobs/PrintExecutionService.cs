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
        var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;

        ticket.PageMediaSize = isA4
            ? new PageMediaSize(PageMediaSizeName.ISOA4)
            : new PageMediaSize(PageMediaSizeName.ISOA3);

        ticket.PageOrientation = isLandscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.InputBin = InputBin.AutoSelect;
        ticket.CopyCount = request.Copies;

        if (request.Profile.RequiresDuplex)
            ticket.Duplexing = request.Profile.UseDuplexShortEdge
                ? Duplexing.TwoSidedShortEdge
                : Duplexing.TwoSidedLongEdge;
        else
            ticket.Duplexing = Duplexing.OneSided;

        if (request.Profile.IsBooklet)
        {
            try
            {
                var caps = queue.GetPrintCapabilities(ticket);
                var staplingCaps = caps.StaplingCapability;

                if (staplingCaps.Contains(Stapling.SaddleStitch))
                {
                    ticket.Stapling = Stapling.SaddleStitch;
                    logger.LogInformation("Stapling set to SaddleStitch");
                }
                else if (staplingCaps.Contains(Stapling.StapleDualLeft))
                {
                    ticket.Stapling = Stapling.StapleDualLeft;
                    logger.LogInformation("Stapling set to StapleDualLeft (2 staples on binding edge)");
                }
                else if (staplingCaps.Contains(Stapling.StapleDualTop))
                {
                    ticket.Stapling = Stapling.StapleDualTop;
                    logger.LogInformation("Stapling set to StapleDualTop (2 staples on binding edge)");
                }
                else
                {
                    ticket.Stapling = Stapling.None;
                    logger.LogInformation("Driver does not support Booklet/Dual stapling; Duplex short-edge fold active");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not query or set stapling on print ticket");
            }
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

        // ── Step 5: send to printer using the UserPrintTicket ───────────────────
        PrintQueue.CreateXpsDocumentWriter(queue).Write(fixedDoc, ticket);

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
