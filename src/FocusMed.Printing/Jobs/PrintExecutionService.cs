using System.Printing;
using System.Text.Json;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using FocusMed.Printing.Imposition;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PDFtoImage;
using PdfSharpCore.Pdf.IO;
using SkiaSharp;

namespace FocusMed.Printing.Jobs;

internal sealed class PrintExecutionService(
    IBookletImpositionService bookletService,
    IRawPrintService rawPrintService,
    IOptions<RawPrinterConfig> rawPrinterConfig,
    ILogger<PrintExecutionService> logger) : IPrintExecutionService
{
    public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken ct = default)
    {
        logger.LogInformation("Print: '{Pdf}' -> '{Printer}' profile='{Profile}' copies={Copies}",
            request.PdfPath, request.PrinterName, request.Profile.Name, request.Copies);

        string pdfPath = request.PdfPath;

        var rawPreset = rawPrinterConfig.Value.Printers
            .FirstOrDefault(p => p.Name.Equals(request.PrinterName, StringComparison.OrdinalIgnoreCase));

        bool useWindowsDriver = rawPreset is not null && !string.IsNullOrEmpty(rawPreset.WindowsPrinterName);

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

        if (useWindowsDriver)
        {
            string resolvedPrinterName = rawPreset!.WindowsPrinterName!;
            logger.LogInformation("Printing via Windows queue '{Printer}' (preset: {Preset}, booklet: {Booklet})",
                resolvedPrinterName, rawPreset.Name, request.Profile.IsBooklet);
            try
            {
                return RunInStaThread(() => PrintViaXps(pdfPath, request with { PrinterName = resolvedPrinterName }));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Windows print failed");
                return new PrintJobResult { Success = false, ErrorMessage = ex.Message };
            }
            finally
            {
                if (pdfPath != request.PdfPath && File.Exists(pdfPath))
                    try { File.Delete(pdfPath); } catch { }
            }
        }

        if (rawPreset is not null)
        {
            try
            {
                var success = await rawPrintService.PrintPdfAsync(rawPreset.Ip, pdfPath, rawPreset.PaperSize,
                    request.Profile.RequiresDuplex, request.Profile.UseDuplexShortEdge, rawPreset.Port, 60000, ct);
                string resolvedPaperSize = rawPreset.PaperSize ?? (request.Profile.PaperSizeName ?? "A3");
                if (success)
                {
                    return new PrintJobResult
                    {
                        Success = true, PaperSizeUsed = resolvedPaperSize, Landscape = false,
                        Duplex = request.Profile.RequiresDuplex, PagesPrinted = CountPdfPages(pdfPath),
                        ImposedPdfPath = request.Profile.IsBooklet ? pdfPath : null,
                        DetectedBookletPaper = request.Profile.IsBooklet ? resolvedPaperSize : null
                    };
                }
                return new PrintJobResult { Success = false, ErrorMessage = "Raw print failed" };
            }
            catch (Exception ex) { return new PrintJobResult { Success = false, ErrorMessage = ex.Message }; }
            finally { if (pdfPath != request.PdfPath && File.Exists(pdfPath)) try { File.Delete(pdfPath); } catch { } }
        }

        try { return RunInStaThread(() => PrintViaXps(pdfPath, request)); }
        catch (Exception ex) { return new PrintJobResult { Success = false, ErrorMessage = ex.Message }; }
        finally { if (pdfPath != request.PdfPath && File.Exists(pdfPath)) try { File.Delete(pdfPath); } catch { } }
    }

    private static int CountPdfPages(string pdfPath)
    {
        try { using var d = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import); return d.Pages.Count; }
        catch { return 0; }
    }

    private PrintJobResult PrintViaXps(string pdfPath, PrintJobRequest request)
    {
        double pageWpt, pageHpt;
        int pageCount;
        using (var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import))
        {
            pageCount = doc.Pages.Count;
            pageWpt = doc.Pages[0].Width;
            pageHpt = doc.Pages[0].Height;
        }

        double wpfW = pageWpt * 96.0 / 72.0;
        double wpfH = pageHpt * 96.0 / 72.0;
        double mmW = pageWpt * 25.4 / 72.0;
        double mmH = pageHpt * 25.4 / 72.0;
        bool isLandscape = mmW > mmH;

        using var printServer = new LocalPrintServer();
        using var queue = printServer.GetPrintQueue(request.PrinterName);

        var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
        if (ticket is null)
        {
            logger.LogWarning("Printer '{Printer}' returned no usable PrintTicket; falling back to a default ticket.", request.PrinterName);
            ticket = new PrintTicket();
        }

        bool useA3 = (Math.Abs(mmW - 297) < 2 && Math.Abs(mmH - 420) < 2)
                   || (Math.Abs(mmW - 420) < 2 && Math.Abs(mmH - 297) < 2);
        ticket.PageMediaSize = useA3
            ? new PageMediaSize(PageMediaSizeName.ISOA3)
            : new PageMediaSize(PageMediaSizeName.ISOA4);
        ticket.PageOrientation = isLandscape ? PageOrientation.Landscape : PageOrientation.Portrait;
        ticket.InputBin = InputBin.AutoSelect;
        ticket.CopyCount = request.Copies;

        if (request.Profile.IsBooklet)
        {
            ticket.Stapling = Stapling.SaddleStitch;
            ticket.Duplexing = Duplexing.TwoSidedShortEdge;
            ticket = InjectKonicaBookletFinishing(ticket);
        }
        else
        {
            ticket.Stapling = Stapling.None;
            if (request.Profile.RequiresDuplex)
                ticket.Duplexing = request.Profile.UseDuplexShortEdge
                    ? Duplexing.TwoSidedShortEdge : Duplexing.TwoSidedLongEdge;
            else
                ticket.Duplexing = Duplexing.OneSided;
        }

        logger.LogInformation("Page: {W}x{H}mm booklet={Booklet}", mmW, mmH, request.Profile.IsBooklet);

        byte[] pdfBytes = File.ReadAllBytes(pdfPath);

        // Streaming render: the XPS writer pulls pages one at a time through the paginator,
        // so only a single 300-DPI bitmap (~70MB per A3 page) is alive at any moment.
        // The old approach materialized every page first — multi-GB transient memory on big booklets.
        var paginator = new PdfPagePaginator(
            pdfBytes, pageCount, new System.Windows.Size(wpfW, wpfH), request.ForceGrayscale);

        PrintQueue.CreateXpsDocumentWriter(queue).Write(paginator, ticket);

        string resolvedPaperSize = useA3 ? "A3" : "A4";

        logger.LogInformation("Sent: {Pages} pages {W}x{H}mm -> '{Printer}'",
            pageCount, mmW, mmH, request.PrinterName);

        return new PrintJobResult
        {
            Success = true, PaperSizeUsed = resolvedPaperSize, Landscape = isLandscape,
            Duplex = request.Profile.RequiresDuplex, PagesPrinted = pageCount,
            ImposedPdfPath = request.Profile.IsBooklet ? pdfPath : null,
            DetectedBookletPaper = request.Profile.IsBooklet ? resolvedPaperSize : null
        };
    }

    private sealed class PdfPagePaginator : System.Windows.Documents.DocumentPaginator
    {
        private readonly byte[] _pdfBytes;
        private readonly int _pageCount;
        private readonly System.Windows.Size _pageSize;
        private readonly bool _grayscale;

        public PdfPagePaginator(byte[] pdfBytes, int pageCount, System.Windows.Size pageSize, bool grayscale)
        {
            _pdfBytes = pdfBytes;
            _pageCount = pageCount;
            _pageSize = pageSize;
            _grayscale = grayscale;
        }

        public override System.Windows.Documents.DocumentPage GetPage(int pageNumber)
        {
            using var ms = new MemoryStream(_pdfBytes, writable: false);
            using var skBitmap = Conversion.ToImage(ms, page: pageNumber, options: new RenderOptions
            {
                Dpi = 300, Grayscale = _grayscale, BackgroundColor = SKColors.White
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

            var page = new FixedPage { Width = _pageSize.Width, Height = _pageSize.Height };
            page.Children.Add(new System.Windows.Controls.Image
            {
                Source = bmp, Width = _pageSize.Width, Height = _pageSize.Height,
                Stretch = System.Windows.Media.Stretch.Fill
            });
            page.Measure(_pageSize);
            page.Arrange(new System.Windows.Rect(new System.Windows.Point(), _pageSize));
            page.UpdateLayout();

            return new System.Windows.Documents.DocumentPage(
                page, _pageSize,
                new System.Windows.Rect(new System.Windows.Point(), _pageSize),
                new System.Windows.Rect(new System.Windows.Point(), _pageSize));
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pageCount;
        public override System.Windows.Size PageSize { get => _pageSize; set { } }
        public override System.Windows.Documents.IDocumentPaginatorSource? Source => null;
    }

    private const string PsfNs = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";

    private PrintTicket InjectKonicaBookletFinishing(PrintTicket ticket)
    {
        var ms = ticket.GetXmlStream();
        if (ms is null) { logger.LogWarning("Booklet: no XML stream"); return ticket; }
        using var sr = new System.IO.StreamReader(ms);
        var ticketXml = sr.ReadToEnd();

        var doc = XDocument.Parse(ticketXml);
        var psf = XNamespace.Get(PsfNs);

        var propParam = doc.Root?
            .Elements(psf + "ParameterInit")
            .FirstOrDefault(e => (e.Attribute("name")?.Value ?? "").Contains("JobKMJobCustomProperties000"));

        if (propParam is not null)
        {
            var valueElem = propParam.Element(psf + "Value");
            if (valueElem is not null)
            {
                try
                {
                    var jsonData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valueElem.Value);
                    if (jsonData is not null && jsonData.TryGetValue("features", out var featuresElem))
                    {
                        var features = JsonSerializer.Deserialize<Dictionary<string, string>>(featuresElem.GetRawText());
                        if (features is not null)
                        {
                            features["CStapleFold"] = "On";
                            features["Folding"] = "On";
                            var paraminits = jsonData.TryGetValue("paraminits", out var pi)
                                ? pi.GetRawText() : "{}";
                            valueElem.Value = "{\"features\":" + JsonSerializer.Serialize(features) + ",\"paraminits\":" + paraminits + "}";
                            logger.LogInformation("Booklet: CStapleFold=On, Folding=On injected into Konica PrintTicket");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Booklet: failed to modify KM properties");
                }
            }
        }

        using var outMs = new MemoryStream();
        doc.Save(outMs);
        outMs.Position = 0;
        return new PrintTicket(outMs);
    }

    private const int StaPrintTimeoutSeconds = 180;

    private static T RunInStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? ex = null;
        var completed = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception e) { ex = e; }
            finally { completed.Set(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();

        if (!t.Join(TimeSpan.FromSeconds(StaPrintTimeoutSeconds)))
            throw new TimeoutException(
                $"Print did not complete within {StaPrintTimeoutSeconds}s (spooler hung or printer offline?). " +
                "Background STA thread left running.");

        completed.Wait();
        if (ex != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        return result;
    }
}
