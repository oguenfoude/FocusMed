using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FocusMed.Data;
using FocusMed.Data.Entities;
using FocusMed.Dicom.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FocusMed.Dicom;

public sealed class PrintScuService : IPrintScuService
{
    private readonly ILogger<PrintScuService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public PrintScuService(ILogger<PrintScuService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> SendPrintJobAsync(int printJobId, FilmPrinterConfig printer, CancellationToken ct)
    {
        if (printer is null || !printer.Enabled
            || string.IsNullOrWhiteSpace(printer.PrinterIp)
            || string.IsNullOrWhiteSpace(printer.PrinterAe)
            || printer.PrinterPort <= 0)
        {
            _logger.LogWarning("Printer config invalid for PrintJob {PrintJobId}", printJobId);
            return false;
        }

        PrintJob printJob;
        List<FilmBox> filmBoxes;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
            var job = await db.PrintJobs
                .Include(p => p.FilmBoxes).ThenInclude(fb => fb.ImageBoxes)
                .FirstOrDefaultAsync(p => p.Id == printJobId, ct);
            if (job is null)
            {
                _logger.LogWarning("PrintJob {PrintJobId} not found", printJobId);
                return false;
            }
            printJob = job;
            filmBoxes = printJob.FilmBoxes.ToList();
        }

        try
        {
            var client = DicomClientFactory.Create(printer.PrinterIp, printer.PrinterPort, false, printer.ScuAe, printer.PrinterAe);

            foreach (var box in filmBoxes)
            {
                await SendFilmBoxAsync(client, printJob, box, printer, ct);
            }

            printJob.Status = PrintStatus.Completed;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
                var fresh = await db.PrintJobs.FirstOrDefaultAsync(p => p.Id == printJobId, ct);
                if (fresh is not null)
                {
                    fresh.Status = PrintStatus.Completed;
                    await db.SaveChangesAsync(ct);
                }
            }

            _logger.LogInformation("Print sent: Job #{PrintJobId} -> {PrinterName}", printJobId, printer.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Print failed: Job #{PrintJobId} -> {PrinterName}", printJobId, printer.Name);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
                var fresh = await db.PrintJobs.FirstOrDefaultAsync(p => p.Id == printJobId, ct);
                if (fresh is not null)
                {
                    fresh.Status = PrintStatus.Failed;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception inner)
            {
                _logger.LogWarning(inner, "PrintScu: failed to update PrintJob {PrintJobId} status to Failed.", printJobId);
            }
            return false;
        }
    }

    private async Task SendFilmBoxAsync(IDicomClient client, PrintJob printJob, FilmBox filmBox, FilmPrinterConfig printer, CancellationToken ct)
    {
        var isColor = printer.PrinterType == PrinterType.Multicolor;

        var filmSessionClass = isColor ? DicomUID.BasicColorPrintManagementMeta : DicomUID.BasicGrayscalePrintManagementMeta;
        var imageBoxClass = isColor ? DicomUID.BasicColorImageBox : DicomUID.BasicGrayscaleImageBox;

        var filmSessionInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID();

        var filmSessionDataset = new DicomDataset
        {
            { DicomTag.NumberOfCopies, (ushort)printJob.NumberOfCopies },
            { DicomTag.PrintPriority, printJob.PrintPriority },
            { DicomTag.MediumType, printer.FilmType },
            { DicomTag.FilmDestination, printer.FilmTarget }
        };

        DicomUID? printerFilmSessionUid = null;
        var filmSessionCreate = new DicomNCreateRequest(filmSessionClass, filmSessionInstanceUid)
        {
            Dataset = filmSessionDataset
        };
        filmSessionCreate.OnResponseReceived = (req, rsp) =>
        {
            if (rsp.Status == DicomStatus.Success)
                printerFilmSessionUid = rsp.SOPInstanceUID;
        };
        await client.AddRequestAsync(filmSessionCreate);

        var imageBoxes = filmBox.ImageBoxes.ToList();
        var imageBoxInstanceUids = new List<DicomUID>();

        for (var pos = 0; pos < imageBoxes.Count; pos++)
        {
            var ib = imageBoxes[pos];
            var imageBoxInstanceUid = DicomUIDGenerator.GenerateDerivedFromUUID();

            var imageBoxDataset = new DicomDataset
            {
                { DicomTag.ImageBoxPosition, (ushort)(pos + 1) }
            };

            DicomUID? printerImageBoxUid = null;
            var imageBoxCreate = new DicomNCreateRequest(imageBoxClass, imageBoxInstanceUid)
            {
                Dataset = imageBoxDataset
            };
            imageBoxCreate.OnResponseReceived = (req, rsp) =>
            {
                if (rsp.Status == DicomStatus.Success)
                    printerImageBoxUid = rsp.SOPInstanceUID;
            };
            await client.AddRequestAsync(imageBoxCreate);

            imageBoxInstanceUids.Add(printerImageBoxUid ?? imageBoxInstanceUid);

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FocusMedDbContext>();
                var dicomImage = await db.DicomImages.FirstOrDefaultAsync(di => di.SopInstanceUid == ib.ReferencedImageSopUid, ct);
                if (dicomImage is not null && !string.IsNullOrWhiteSpace(dicomImage.FilePath) && File.Exists(dicomImage.FilePath))
                {
                    var srcFile = await DicomFile.OpenAsync(dicomImage.FilePath);
                    var pixelDataItem = srcFile.Dataset.GetDicomItem<DicomItem>(DicomTag.PixelData);
                    if (pixelDataItem is not null)
                    {
                        var imageSequenceTag = isColor ? DicomTag.BasicColorImageSequence : DicomTag.BasicGrayscaleImageSequence;
                        var innerDataset = new DicomDataset();
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.BitsAllocated, out ushort bitsAllocated))
                            innerDataset.Add(DicomTag.BitsAllocated, bitsAllocated);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.BitsStored, out ushort bitsStored))
                            innerDataset.Add(DicomTag.BitsStored, bitsStored);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.HighBit, out ushort highBit))
                            innerDataset.Add(DicomTag.HighBit, highBit);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.SamplesPerPixel, out ushort samplesPerPixel))
                            innerDataset.Add(DicomTag.SamplesPerPixel, samplesPerPixel);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.Rows, out ushort rows))
                            innerDataset.Add(DicomTag.Rows, rows);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.Columns, out ushort columns))
                            innerDataset.Add(DicomTag.Columns, columns);
                        if (srcFile.Dataset.TryGetSingleValue(DicomTag.PhotometricInterpretation, out string? photometric) && photometric != null)
                            innerDataset.Add(DicomTag.PhotometricInterpretation, photometric);
                        innerDataset.Add(pixelDataItem);
                        var ibSetDataset = new DicomDataset
                        {
                            new DicomSequence(imageSequenceTag, innerDataset)
                        };
                        var imageBoxSet = new DicomNSetRequest(imageBoxClass, imageBoxInstanceUids[^1])
                        {
                            Dataset = ibSetDataset
                        };
                        await client.AddRequestAsync(imageBoxSet);
                    }
                }
            }
        }

        var printAction = new DicomNActionRequest(DicomUID.BasicFilmSession, printerFilmSessionUid ?? filmSessionInstanceUid, 1);
        await client.AddRequestAsync(printAction);

        var deleteSession = new DicomNDeleteRequest(DicomUID.BasicFilmSession, printerFilmSessionUid ?? filmSessionInstanceUid);
        await client.AddRequestAsync(deleteSession);

        await client.SendAsync(ct);
    }
}
