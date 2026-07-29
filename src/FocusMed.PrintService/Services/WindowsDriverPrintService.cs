using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using FocusMed.PrintService.Abstractions;
using FocusMed.PrintService.Configuration;
using Microsoft.Extensions.Options;
using PdfiumPrinter;

namespace FocusMed.PrintService.Services;

public sealed class WindowsDriverPrintService : IPhysicalPrintService
{
    private readonly IOptionsMonitor<PhysicalPrinterOptions> _options;
    private readonly ILogger<WindowsDriverPrintService> _logger;
    private readonly JobStateTracker _tracker;
    private readonly PrinterCapabilityDetector _caps;
    private readonly IBookletImpositionService _booklet;

    public WindowsDriverPrintService(
        IOptionsMonitor<PhysicalPrinterOptions> options,
        ILogger<WindowsDriverPrintService> logger,
        JobStateTracker tracker,
        PrinterCapabilityDetector caps,
        IBookletImpositionService booklet)
    {
        _options = options;
        _logger = logger;
        _tracker = tracker;
        _caps = caps;
        _booklet = booklet;
    }

    public Task<PrintResult> PrintAsync(PrintRequest request)
    {
        var result = PrintInternal(request);
        return Task.FromResult(result);
    }

    public Task<JobStatus> GetJobStatusAsync(string printerName, int jobId)
    {
        return Task.FromResult(_tracker.Get(printerName, jobId));
    }

    public Task<IReadOnlyList<PrinterInfo>> GetConfiguredPrintersAsync()
    {
        IReadOnlyList<PrinterInfo> list = _options.CurrentValue.PhysicalPrinters
            .Where(p => p.Enabled)
            .Select(p =>
            {
                var caps = _caps.Detect(p.Name);
                return new PrinterInfo(p.Name, p.Enabled, p.Protocol, caps.CanDuplex, caps.SupportedPaperSizes.Count);
            })
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<WindowsPrinterInfo>> GetAllWindowsPrintersAsync()
    {
        var printers = PrinterSettings.InstalledPrinters.Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new WindowsPrinterInfo(name, ""))
            .ToList();
        return Task.FromResult<IReadOnlyList<WindowsPrinterInfo>>(printers);
    }

    public Task<PrinterCapabilities> GetCapabilitiesAsync(string printerName)
    {
        var caps = _caps.Detect(printerName);
        return Task.FromResult(caps);
    }

    private PrintResult PrintInternal(PrintRequest req)
    {
        var config = _options.CurrentValue.PhysicalPrinters
            .FirstOrDefault(p => string.Equals(p.Name, req.PrinterName, StringComparison.OrdinalIgnoreCase));

        string? resolvedQueue;
        if (config != null)
        {
            resolvedQueue = _caps.ResolveBestQueue(config);
            if (resolvedQueue == null)
            {
                var available = string.Join(", ", PrinterSettings.InstalledPrinters.Cast<string>());
                return Fail(
                    $"Aucune imprimante Windows ne correspond a '{config.Name}'. " +
                    $"Imprimantes disponibles : {available}.");
            }
        }
        else
        {
            var exactMatch = PrinterSettings.InstalledPrinters.Cast<string>()
                .FirstOrDefault(n => string.Equals(n, req.PrinterName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch == null)
            {
                var available = string.Join(", ", PrinterSettings.InstalledPrinters.Cast<string>());
                return Fail(
                    $"L'imprimante '{req.PrinterName}' est introuvable. " +
                    $"Imprimantes disponibles : {available}.");
            }
            resolvedQueue = exactMatch;
        }

        string resolvedPath;
        try
        {
            resolvedPath = PdfPathResolver.Resolve(req.PdfPath)
                ?? throw new FileNotFoundException($"PDF introuvable : {req.PdfPath}");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        if (!File.Exists(resolvedPath))
            return Fail($"PDF introuvable : {resolvedPath}");

        string printPath = resolvedPath;
        string? bookletTempPath = null;

        if (req.BookletMode)
        {
            var probeSettings = new PrinterSettings { PrinterName = resolvedQueue };
            var paperSize = config != null
                ? PaperSizePolicy.Resolve(config, probeSettings)
                : probeSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(ps => ps.Kind == PaperKind.A4)
                    ?? probeSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault();
            if (paperSize == null)
                return Fail("Impossible de determiner la taille de papier pour le livret.");

            var caps = _caps.Detect(req.PrinterName);
            var hasHorizontal = caps.SupportedDuplexModes.Any(m =>
                string.Equals(m, "Horizontal", StringComparison.OrdinalIgnoreCase));
            if (!hasHorizontal)
                return Fail("Cette imprimante ne supporte pas la reliure sur le petit cote — livret impossible.");

            try
            {
                var sheetSize = new PaperSizeInfo(
                    paperSize.PaperName,
                    (int)(paperSize.Width * 25.4),
                    (int)(paperSize.Height * 25.4),
                    paperSize.Kind.ToString());

                bookletTempPath = _booklet.ComposeBookletAsync(resolvedPath, sheetSize, new BookletOptions(ShortEdgeBinding: true)).GetAwaiter().GetResult();
                printPath = bookletTempPath;

                if (!File.Exists(printPath))
                    return Fail("La generation du livret a echoue — fichier temporaire non cree.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Booklet imposition failed for {Printer}", req.PrinterName);
                return Fail($"Echec de la generation du livret : {ex.GetBaseException().Message}");
            }
        }

        var jobId = _tracker.NextId();
        var printerName = req.PrinterName;
        _tracker.Register(printerName, jobId);
        _tracker.MarkPrinting(printerName, jobId);

        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var doc = PdfDocument.Load(printPath);

                var pageBitmaps = new List<Image>();
                try
                {
                    for (int i = 0; i < doc.PageCount; i++)
                    {
                        using var bitmap = doc.Render(i, 300f, 300f,
                            PdfRenderFlags.ForPrinting | PdfRenderFlags.CorrectFromDpi);
                        var copy = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        using (var g = System.Drawing.Graphics.FromImage(copy))
                        {
                            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
                        }
                        pageBitmaps.Add(copy);
                    }

                    var currentPage = 0;
                    var printDoc = new PrintDocument();
                    printDoc.DocumentName = req.BookletMode
                        ? $"FocusMed-Livret-{jobId:D6}"
                        : $"FocusMed-{jobId:D6}";

                    printDoc.PrinterSettings = new PrinterSettings
                    {
                        PrinterName = resolvedQueue,
                        Copies = (short)req.Copies,
                    };

                    var a4 = printDoc.PrinterSettings.PaperSizes.Cast<PaperSize>()
                        .FirstOrDefault(ps => ps.Kind == PaperKind.A4)
                        ?? printDoc.PrinterSettings.PaperSizes.Cast<PaperSize>()
                            .OrderBy(p => Math.Abs(p.Width - 827) + Math.Abs(p.Height - 1169))
                            .FirstOrDefault();
                    if (a4 != null)
                        printDoc.DefaultPageSettings.PaperSize = a4;

                    printDoc.PrinterSettings.Duplex = req.BookletMode
                        ? Duplex.Horizontal
                        : req.Duplex
                            ? Duplex.Vertical
                            : Duplex.Simplex;

                    _logger.LogInformation(
                        "Printing PDF: queue={Queue}, pages={Pages}, paper={Paper}, duplex={Duplex}, booklet={Booklet}, copies={Copies}",
                        resolvedQueue, pageBitmaps.Count, a4?.PaperName ?? "default",
                        printDoc.PrinterSettings.Duplex, req.BookletMode, req.Copies);

                    printDoc.PrintPage += (sender, e) =>
                    {
                        if (currentPage < pageBitmaps.Count)
                        {
                            var img = pageBitmaps[currentPage];

                            var pageW = e.PageBounds.Width;
                            var pageH = e.PageBounds.Height;

                            var imgAspect = (double)img.Width / img.Height;
                            var pageAspect = pageW / pageH;

                            float drawW, drawH;
                            if (imgAspect > pageAspect)
                            {
                                drawW = pageW;
                                drawH = (float)(pageW / imgAspect);
                            }
                            else
                            {
                                drawH = pageH;
                                drawW = (float)(pageH * imgAspect);
                            }

                            var x = (pageW - drawW) / 2f + e.MarginBounds.Left - e.PageBounds.Left;
                            var y = (pageH - drawH) / 2f + e.MarginBounds.Top - e.PageBounds.Top;

                            e.Graphics?.DrawImage(img, x, y, drawW, drawH);
                            currentPage++;

                            if (currentPage < pageBitmaps.Count)
                                e.HasMorePages = true;
                        }
                    };

                    printDoc.Print();
                    printDoc.Dispose();
                }
                finally
                {
                    foreach (var img in pageBitmaps)
                        img.Dispose();
                }

                _tracker.MarkCompleted(printerName, jobId);
                _logger.LogInformation(
                    "Print job completed: job={JobId}, queue={Queue}",
                    jobId, resolvedQueue);
            }
            catch (Exception ex)
            {
                threadException = ex;
                _logger.LogError(ex, "Print failed for {Queue}", resolvedQueue);
                _tracker.MarkError(printerName, jobId, ex.GetBaseException().Message);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (threadException != null)
            return Fail(threadException.GetBaseException().Message);

        if (thread.IsAlive)
        {
            _logger.LogWarning("Print thread still running after 60s timeout for job {JobId}", jobId);
            return new PrintResult(true, jobId, null);
        }

        var status = _tracker.Get(printerName, jobId);
        if (status.State == "Error")
            return Fail(status.ErrorMessage ?? "Echec de l'impression.");

        if (bookletTempPath != null)
        {
            try { if (File.Exists(bookletTempPath)) File.Delete(bookletTempPath); } catch { }
        }

        return new PrintResult(true, jobId, null);
    }

    private static PrintResult Fail(string message)
        => new(false, null, message);
}
