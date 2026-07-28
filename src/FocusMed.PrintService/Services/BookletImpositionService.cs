using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using FocusMed.PrintService.Abstractions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

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

        using var srcDoc = PdfiumPrinter.PdfDocument.Load(sourcePdfPath);
        var pageCount = srcDoc.PageCount;

        if (pageCount == 0)
            throw new InvalidOperationException("Le PDF source ne contient aucune page.");

        var paddedCount = pageCount;
        if (paddedCount % 4 != 0)
            paddedCount += 4 - (paddedCount % 4);

        var sheetWidthPt = targetSheetSize.WidthHundredthsMm / 100.0 * 72.0 / 25.4;
        var sheetHeightPt = targetSheetSize.HeightHundredthsMm / 100.0 * 72.0 / 25.4;

        var portraitW = Math.Min(sheetWidthPt, sheetHeightPt);
        var portraitH = Math.Max(sheetWidthPt, sheetHeightPt);
        var halfH = portraitH / 2.0;

        _logger.LogInformation(
            "Booklet imposition: {SourcePages} pages -> {PaddedPages} padded, portrait {W}x{H}pt ({WMm}x{HMm}mm), half-height {HalfH}pt ({HalfHMm}mm)",
            pageCount, paddedCount, portraitW, portraitH,
            (portraitW / 72.0 * 25.4).ToString("F1"), (portraitH / 72.0 * 25.4).ToString("F1"),
            halfH, (halfH / 72.0 * 25.4).ToString("F1"));

        var outputDoc = new PdfDocument();

        for (int sheet = 0; sheet < paddedCount / 4; sheet++)
        {
            int frontTop = paddedCount - (sheet * 2);
            int frontBottom = sheet * 2 + 1;
            int backTop = sheet * 2 + 2;
            int backBottom = paddedCount - (sheet * 2 + 1);

            RenderPortraitSide(outputDoc, srcDoc, pageCount, portraitW, portraitH, halfH, frontTop, frontBottom);
            RenderPortraitSide(outputDoc, srcDoc, pageCount, portraitW, portraitH, halfH, backTop, backBottom);
        }

        var outputPath = Path.Combine(Path.GetTempPath(), $"booklet_{Guid.NewGuid():N}.pdf");
        outputDoc.Save(outputPath);

        _logger.LogInformation("Booklet PDF created: {Path} ({SheetCount} sheets, {PageCount} pages)",
            outputPath, paddedCount / 4, outputDoc.PageCount);

        return Task.FromResult(outputPath);
    }

    private void RenderPortraitSide(
        PdfDocument outputDoc,
        PdfiumPrinter.PdfDocument srcDoc,
        int sourcePageCount,
        double sheetW,
        double sheetH,
        double halfH,
        int topPageIndex,
        int bottomPageIndex)
    {
        var page = new PdfPage { Width = sheetW, Height = sheetH };
        outputDoc.AddPage(page);

        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
        DrawHalfPage(gfx, srcDoc, sourcePageCount, topPageIndex, 0, 0, sheetW, halfH);
        DrawHalfPage(gfx, srcDoc, sourcePageCount, bottomPageIndex, 0, halfH, sheetW, halfH);
    }

    private void DrawHalfPage(
        XGraphics gfx,
        PdfiumPrinter.PdfDocument srcDoc,
        int sourcePageCount,
        int pageIndex,
        double x,
        double y,
        double width,
        double height)
    {
        if (pageIndex < 1 || pageIndex > sourcePageCount)
        {
            gfx.DrawRectangle(XBrushes.White, x, y, width, height);
            return;
        }

        using var bitmap = srcDoc.Render(pageIndex - 1, 300f, 300f, PdfiumPrinter.PdfRenderFlags.ForPrinting | PdfiumPrinter.PdfRenderFlags.CorrectFromDpi);
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var streamFunc = new Func<Stream>(() => new MemoryStream(ms.ToArray()));
        using var xImage = XImage.FromStream(streamFunc);

        gfx.DrawImage(xImage, x, y, width, height);
    }
}
