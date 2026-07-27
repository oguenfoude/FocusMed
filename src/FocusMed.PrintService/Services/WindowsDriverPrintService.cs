using System.Drawing.Printing;
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

    public WindowsDriverPrintService(
        IOptionsMonitor<PhysicalPrinterOptions> options,
        ILogger<WindowsDriverPrintService> logger,
        JobStateTracker tracker)
    {
        _options = options;
        _logger = logger;
        _tracker = tracker;
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
            .Select(p => new PrinterInfo(p.Name, p.Enabled, p.Protocol))
            .ToArray();
        return Task.FromResult(list);
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

        var probeSettings = new PrinterSettings
        {
            PrinterName = config.WindowsQueueName
        };
        if (!probeSettings.IsValid)
        {
            var available = string.Join(", ",
                PrinterSettings.InstalledPrinters.Cast<string>());
            return Fail(
                $"File d'attente '{config.WindowsQueueName}' introuvable. " +
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
                $"Aucune taille de papier utilisable n'a ete trouvee sur '{config.WindowsQueueName}'. " +
                $"Tailles detectees : {availableSizes}.");
        }

        var jobId = _tracker.NextId();
        var printerName = config.Name;
        _tracker.Register(printerName, jobId);

        try
        {
            using var doc = PdfDocument.Load(resolvedPath);

            var pdfSettings = new PdfPrintSettings(PdfPrintMode.ShrinkToMargin, multiplePages: null);

            using var printDoc = doc.CreatePrintDocument(pdfSettings);

            printDoc.PrinterSettings = probeSettings;
            printDoc.PrinterSettings.Copies = (short)Math.Clamp(req.Copies, 1, 99);
            printDoc.PrinterSettings.Duplex = req.Duplex
                ? Duplex.Vertical
                : Duplex.Simplex;
            printDoc.DefaultPageSettings.PaperSize = paperSize;
            printDoc.DocumentName = $"FocusMed-{jobId:D6}";
            printDoc.OriginAtMargins = true;

            printDoc.BeginPrint += (_, _) => _tracker.MarkPrinting(printerName, jobId);
            printDoc.EndPrint += (_, _) => _tracker.MarkCompleted(printerName, jobId);

            printDoc.Print();

            return new PrintResult(true, jobId, null);
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            _logger.LogError(ex, "Print failed for {Printer} (job #{JobId}) file={Path}",
                printerName, jobId, resolvedPath);
            _tracker.MarkError(printerName, jobId, message);
            return Fail(message);
        }
    }

    private static PrintResult Fail(string message)
        => new(false, null, message);
}
