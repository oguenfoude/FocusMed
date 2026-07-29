using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Printing;
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
        // Find config if it exists, otherwise use printer name directly
        var config = _options.CurrentValue.PhysicalPrinters
            .FirstOrDefault(p => string.Equals(p.Name, req.PrinterName, StringComparison.OrdinalIgnoreCase));

        // Resolve the actual Windows queue name
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
            // Not in appsettings — check if it exists as a Windows printer directly
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

        try
        {
            using var doc = PdfDocument.Load(printPath);
            var pageCount = doc.PageCount;

            _logger.LogInformation(
                "Rendering PDF to bitmaps: file={Path}, pages={Pages}, booklet={Booklet}",
                printPath, pageCount, req.BookletMode);

            var images = new List<byte[]>();
            int pageWidthPx = 0, pageHeightPx = 0;
            for (int i = 0; i < pageCount; i++)
            {
                using var bitmap = doc.Render(i, 300f, 300f,
                    PdfRenderFlags.ForPrinting | PdfRenderFlags.CorrectFromDpi);
                if (i == 0)
                {
                    pageWidthPx = bitmap.Width;
                    pageHeightPx = bitmap.Height;
                }
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                images.Add(ms.ToArray());
            }

            _logger.LogInformation(
                "Building XPS from {Count} images ({Width}x{Height}px)",
                images.Count, pageWidthPx, pageHeightPx);

            var xpsBytes = XpsBuilder.CreateXpsFromPngImages(images, pageWidthPx, pageHeightPx);

            var duplexMode = req.BookletMode
                ? DuplexMode.ShortEdge
                : req.Duplex
                    ? DuplexMode.LongEdge
                    : DuplexMode.Simplex;

            var xpsWithTicket = XpsBuilder.InjectPrintTicket(xpsBytes, duplexMode, req.Copies);

            _logger.LogInformation(
                "Submitting XPS to printer: queue={Queue}, duplex={Duplex}, booklet={Booklet}, copies={Copies}",
                resolvedQueue, req.Duplex, req.BookletMode, req.Copies);

            PrintResult result = Fail("Thread not executed");
            var thread = new Thread(() =>
            {
                try
                {
                    var server = new LocalPrintServer();
                    var queue = server.GetPrintQueues().FirstOrDefault(q =>
                        string.Equals(q.Name, resolvedQueue, StringComparison.OrdinalIgnoreCase));

                    if (queue == null)
                    {
                        result = Fail($"File d'attente '{resolvedQueue}' introuvable via System.Printing.");
                        return;
                    }

                    var jobName = req.BookletMode
                        ? $"FocusMed-Livret-{jobId:D6}"
                        : $"FocusMed-{jobId:D6}";

                    _tracker.MarkPrinting(printerName, jobId);

                    var job = queue.AddJob(jobName);
                    using (var stream = job.JobStream)
                    {
                        stream.Write(xpsWithTicket, 0, xpsWithTicket.Length);
                    }

                    _tracker.MarkCompleted(printerName, jobId);
                    _logger.LogInformation(
                        "XPS print job submitted: job={JobId}, queue={Queue}, duplex={Duplex}",
                        job.JobIdentifier, resolvedQueue, duplexMode);

                    result = new PrintResult(true, job.JobIdentifier, null);
                }
                catch (Exception ex)
                {
                    var message = ex.GetBaseException().Message;
                    _logger.LogError(ex, "XPS print failed for {Queue}", resolvedQueue);
                    _tracker.MarkError(printerName, jobId, message);
                    result = Fail(message);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            return result!;
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            _logger.LogError(ex, "Print failed for {Printer} (job #{JobId}) file={Path}",
                printerName, jobId, printPath);
            _tracker.MarkError(printerName, jobId, message);
            return Fail(message);
        }
        finally
        {
            if (bookletTempPath != null)
            {
                try { if (File.Exists(bookletTempPath)) File.Delete(bookletTempPath); } catch { }
            }
        }
    }

    private static PrintResult Fail(string message)
        => new(false, null, message);
}
