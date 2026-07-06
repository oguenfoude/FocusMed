using FocusMed.Dicom.Options;

namespace FocusMed.Dicom;

public interface IPrintScuService
{
    Task<bool> SendPrintJobAsync(int printJobId, FilmPrinterConfig printer, CancellationToken ct);
}
