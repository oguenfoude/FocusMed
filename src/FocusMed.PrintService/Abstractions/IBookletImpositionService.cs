using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Abstractions;

public interface IBookletImpositionService
{
    /// <summary>
    /// Takes a source PDF (e.g. A4 pages) and produces a NEW temporary PDF
    /// where every physical sheet holds 2 logical pages, reordered into
    /// correct booklet signature order (last+first, second-last+second, ...),
    /// scaled to fit the target sheet size.
    /// </summary>
    Task<string> ComposeBookletAsync(
        string sourcePdfPath,
        PaperSizeInfo targetSheetSize,
        BookletOptions options);
}

public record BookletOptions(bool ShortEdgeBinding = true);
