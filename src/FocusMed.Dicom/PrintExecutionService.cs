using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FocusMed.Dicom;

public class PrintExecutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPrintScuService _printScu;
    private readonly IOptions<DicomNetworkingOptions> _networkingOptions;
    private readonly ILogger<PrintExecutionService> _logger;

    public PrintExecutionService(
        IServiceScopeFactory scopeFactory,
        IPrintScuService printScu,
        IOptions<DicomNetworkingOptions> networkingOptions,
        ILogger<PrintExecutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _printScu = printScu;
        _networkingOptions = networkingOptions;
        _logger = logger;
    }

    public async Task<bool> ExecutePendingPrintJobAsync(int printJobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();

        var job = await db.PrintJobs
            .Include(j => j.FilmBoxes)
            .FirstOrDefaultAsync(j => j.Id == printJobId, ct);

        if (job is null)
        {
            _logger.LogWarning("PrintExecutionService: PrintJob {Id} not found.", printJobId);
            return false;
        }

        if (job.Status != PrintStatus.Pending)
        {
            _logger.LogWarning("PrintExecutionService: PrintJob {Id} not Pending (current: {Status}), skipping.", printJobId, job.Status);
            return false;
        }

        var matchedPrinter = SelectPrinter();
        if (matchedPrinter is null)
        {
            _logger.LogError("PrintExecutionService: No enabled FilmPrinter configured for PrintJob {Id}.", printJobId);
            job.Status = PrintStatus.Failed;
            await db.SaveChangesAsync(ct);
            return false;
        }

        return await _printScu.SendPrintJobAsync(printJobId, matchedPrinter, ct);
    }

    private FilmPrinterConfig? SelectPrinter()
    {
        var printers = _networkingOptions.Value.FilmPrinters;
        if (printers.Count == 0) return null;

        var enabled = printers.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0) return null;

        return enabled.FirstOrDefault();
    }
}
