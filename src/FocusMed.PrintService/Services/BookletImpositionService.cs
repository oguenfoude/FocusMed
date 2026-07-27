using FocusMed.PrintService.Abstractions;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace FocusMed.PrintService.Services;

public sealed class BookletImpositionService : IBookletImpositionService
{
    private readonly ILogger<BookletImpositionService> _logger;

    public BookletImpositionService(ILogger<BookletImpositionService> logger)
    {
        _logger = logger;
    }

    public Task<string> ComposeBookletAsync(
        string sourcePdfPath,
        PaperSizeInfo targetSheetSize,
        BookletOptions options)
    {
        if (!File.Exists(sourcePdfPath))
            throw new FileNotFoundException($"PDF source introuvable : {sourcePdfPath}");

        using var sourceDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        var pageCount = sourceDoc.PageCount;

        if (pageCount == 0)
            throw new InvalidOperationException("Le PDF source ne contient aucune page.");

        var paddedCount = pageCount;
        if (paddedCount % 4 != 0)
            paddedCount += 4 - (paddedCount % 4);

        _logger.LogInformation(
            "Booklet imposition: {SourcePages} pages -> {PaddedPages} padded, target sheet {SheetW}x{SheetH} hundredths-mm",
            pageCount, paddedCount, targetSheetSize.WidthHundredthsMm, targetSheetSize.HeightHundredthsMm);

        var outputDoc = new PdfDocument();

        var sheetWidthPt = targetSheetSize.WidthHundredthsMm / 100.0 * 72.0 / 25.4;
        var sheetHeightPt = targetSheetSize.HeightHundredthsMm / 100.0 * 72.0 / 25.4;

        for (int sheet = 0; sheet < paddedCount / 4; sheet++)
        {
            int leftBack = paddedCount - (sheet * 2);
            int rightFront = sheet * 2 + 1;
            int leftFront = sheet * 2 + 2;
            int rightBack = paddedCount - (sheet * 2 + 1);

            AddPageToOutput(outputDoc, sourceDoc, pageCount, rightFront, sheetWidthPt, sheetHeightPt);
            AddPageToOutput(outputDoc, sourceDoc, pageCount, leftBack, sheetWidthPt, sheetHeightPt);
            AddPageToOutput(outputDoc, sourceDoc, pageCount, leftFront, sheetWidthPt, sheetHeightPt);
            AddPageToOutput(outputDoc, sourceDoc, pageCount, rightBack, sheetWidthPt, sheetHeightPt);
        }

        var outputPath = Path.Combine(Path.GetTempPath(), $"booklet_{Guid.NewGuid():N}.pdf");
        outputDoc.Save(outputPath);

        _logger.LogInformation("Booklet PDF created: {Path} ({SheetCount} sheets, {PageCount} pages)",
            outputPath, paddedCount / 4, outputDoc.PageCount);

        return Task.FromResult(outputPath);
    }

    private void AddPageToOutput(
        PdfDocument outputDoc,
        PdfDocument sourceDoc,
        int sourcePageCount,
        int pageIndex,
        double targetWidth,
        double targetHeight)
    {
        if (pageIndex < 1 || pageIndex > sourcePageCount)
        {
            var blankPage = new PdfPage
            {
                Width = targetWidth,
                Height = targetHeight
            };
            outputDoc.AddPage(blankPage);
            return;
        }

        var srcPage = sourceDoc.Pages[pageIndex - 1];
        var imported = outputDoc.AddPage(srcPage);
        imported.Width = targetWidth;
        imported.Height = targetHeight;
    }
}
