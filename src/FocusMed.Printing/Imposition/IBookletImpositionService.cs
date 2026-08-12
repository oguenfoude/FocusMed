namespace FocusMed.Printing.Imposition;

public interface IBookletImpositionService
{
    Task<string> ComposeBookletAsync(string inputPdfPath, string targetPaperSize = "A3", CancellationToken ct = default);
}
