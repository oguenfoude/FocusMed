using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
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

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr hPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto)]
    private static extern int DocumentProperties(IntPtr hWnd, IntPtr hPrinter, string pDeviceName,
        IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    private const int DM_OUT_BUFFER = 0x02;
    private const int DM_IN_BUFFER = 0x08;

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

    public Task<PrinterCapabilities> GetCapabilitiesAsync(string printerName)
    {
        var caps = _caps.Detect(printerName);
        return Task.FromResult(caps);
    }

    private PrintResult PrintInternal(PrintRequest req)
    {
        PhysicalPrinterConfig config;
        try
        {
            config = _options.CurrentValue.PhysicalPrinters
                .FirstOrDefault(p => string.Equals(p.Name, req.PrinterName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"L'imprimante configuree '{req.PrinterName}' est introuvable. " +
                    $"Configurez-la dans appsettings.json (section PhysicalPrinters).");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        var resolvedQueue = _caps.ResolveBestQueue(config);
        if (resolvedQueue == null)
        {
            var available = string.Join(", ",
                PrinterSettings.InstalledPrinters.Cast<string>());
            return Fail(
                $"Aucune imprimante Windows ne correspond a '{config.Name}'. " +
                $"Imprimantes disponibles : {available}.");
        }

        string resolvedPath;
        try
        {
            resolvedPath = PdfPathResolver.Resolve(req.PdfPath)
                ?? throw new FileNotFoundException(
                    $"PDF introuvable : {req.PdfPath}");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        var probeSettings = new PrinterSettings { PrinterName = resolvedQueue };
        if (!probeSettings.IsValid)
        {
            var available = string.Join(", ",
                PrinterSettings.InstalledPrinters.Cast<string>());
            return Fail(
                $"File d'attente '{resolvedQueue}' introuvable. " +
                $"Imprimantes Windows disponibles : {available}. " +
                $"Corrigez appsettings.json (Propriete WindowsQueueName) avec le nom exact de la file.");
        }

        if (!File.Exists(resolvedPath))
            return Fail($"PDF introuvable : {resolvedPath}");

        var paperSize = PaperSizePolicy.Resolve(config, probeSettings);
        if (paperSize == null)
        {
            var availableSizes = string.Join(", ", PaperSizePolicy.AvailablePaperSizes(probeSettings));
            return Fail(
                $"Aucune taille de papier utilisable n'a ete trouvee sur '{resolvedQueue}'. " +
                $"Tailles detectees : {availableSizes}.");
        }

        if (req.BookletMode)
        {
            var caps = _caps.Detect(req.PrinterName);
            var hasHorizontal = caps.SupportedDuplexModes.Any(m =>
                string.Equals(m, "Horizontal", StringComparison.OrdinalIgnoreCase));
            if (!hasHorizontal)
            {
                return Fail(
                    $"Cette imprimante ne supporte pas la reliure sur le petit cote — livret impossible. " +
                    $"Utilisez une imprimante avec recto-verso court (Horizontal).");
            }
        }

        string printPath = resolvedPath;
        string? bookletTempPath = null;

        if (req.BookletMode)
        {
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
                _logger.LogError(ex, "Booklet imposition failed for {Printer}", config.Name);
                return Fail($"Echec de la generation du livret : {ex.GetBaseException().Message}");
            }
        }

        var jobId = _tracker.NextId();
        var printerName = config.Name;
        _tracker.Register(printerName, jobId);

        try
        {
            using var doc = PdfDocument.Load(printPath);
            var pageCount = doc.PageCount;

            _logger.LogInformation(
                "Printing: file={Path}, pages={Pages}, booklet={Booklet}, paper={Paper}, duplex={Duplex}",
                printPath, pageCount, req.BookletMode, paperSize.PaperName,
                req.BookletMode ? "Horizontal" : req.Duplex ? "Vertical" : "Simplex");

            var pdfSettings = new PdfPrintSettings(PdfPrintMode.ShrinkToMargin, multiplePages: null);

            using var printDoc = doc.CreatePrintDocument(pdfSettings);

            printDoc.PrinterSettings.PrinterName = resolvedQueue;
            printDoc.PrinterSettings.Copies = (short)Math.Clamp(req.Copies, 1, 99);
            printDoc.PrinterSettings.Duplex = req.BookletMode
                ? Duplex.Horizontal
                : req.Duplex
                    ? Duplex.Vertical
                    : Duplex.Simplex;
            printDoc.DefaultPageSettings.PaperSize = paperSize;
            printDoc.DefaultPageSettings.Landscape = false;
            printDoc.DocumentName = req.BookletMode ? $"FocusMed-Livret-{jobId:D6}" : $"FocusMed-{jobId:D6}";
            printDoc.OriginAtMargins = false;

            ResetDriverDevMode(printDoc, resolvedQueue);

            _logger.LogInformation(
                "Print: file={Path}, booklet={Booklet}, paper={Paper} ({PaperW}x{PaperH}), landscape={Landscape}, duplex={Duplex}, originAtMargins={OriginAtMargins}",
                printPath, req.BookletMode, paperSize.PaperName, paperSize.Width, paperSize.Height,
                printDoc.DefaultPageSettings.Landscape, printDoc.PrinterSettings.Duplex, printDoc.OriginAtMargins);

            printDoc.BeginPrint += (_, _) => _tracker.MarkPrinting(printerName, jobId);
            printDoc.EndPrint += (_, _) => _tracker.MarkCompleted(printerName, jobId);

            printDoc.Print();

            return new PrintResult(true, jobId, null);
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

    private void ResetDriverDevMode(PrintDocument printDoc, string queueName)
    {
        try
        {
            if (!OpenPrinter(queueName, out var hPrinter, IntPtr.Zero))
            {
                _logger.LogWarning("OpenPrinter failed for {Queue}", queueName);
                return;
            }

            try
            {
                int bufSize = DocumentProperties(IntPtr.Zero, hPrinter, queueName, IntPtr.Zero, IntPtr.Zero, 0);
                if (bufSize <= 0)
                {
                    _logger.LogWarning("DocumentProperties buffer size failed for {Queue}", queueName);
                    return;
                }

                IntPtr pDevMode = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int dmResult = DocumentProperties(IntPtr.Zero, hPrinter, queueName, pDevMode, IntPtr.Zero, DM_OUT_BUFFER);
                    if (dmResult > 0)
                    {
                        var setHdevmode = typeof(PrinterSettings).GetMethod("SetHdevmode",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        setHdevmode?.Invoke(printDoc.PrinterSettings, new object[] { pDevMode });
                        _logger.LogInformation("Reset driver DEVMODE for {Queue} (buffer={Size})", queueName, bufSize);
                    }
                    else
                    {
                        _logger.LogWarning("DocumentProperties GET failed for {Queue}: {Result}", queueName, dmResult);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pDevMode);
                }
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reset driver DEVMODE for {Queue}", queueName);
        }
    }

    private static PrintResult Fail(string message)
        => new(false, null, message);
}
