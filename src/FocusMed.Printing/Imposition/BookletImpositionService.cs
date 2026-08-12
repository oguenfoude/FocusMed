using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace FocusMed.Printing.Imposition;

/// <summary>
/// Booklet imposition: A4 portrait source pages -> A3 landscape sheets.
/// Each A3 landscape sheet (420x297mm) holds 2 A4 portrait pages (210x297mm) side by side.
/// Source pages are auto-rotated to portrait if landscape.
/// Page ordering follows standard booklet: fold in center, page 1 = front cover.
/// </summary>
internal sealed class BookletImpositionService(ILogger<BookletImpositionService> logger) : IBookletImpositionService
{
    public Task<string> ComposeBookletAsync(string inputPdfPath, string targetPaperSize = "A3", CancellationToken ct = default)
    {
        var absoluteInputPath = Path.GetFullPath(inputPdfPath);
        var outputDoc = new PdfDocument();
        outputDoc.PageLayout = PdfPageLayout.SinglePage;

        try
        {
            using (var form = XPdfForm.FromFile(absoluteInputPath))
            {
                int inputPages = form.PageCount;
                if (inputPages < 1)
                {
                    throw new InvalidOperationException("PDF has no pages");
                }

                if (inputPages == 1)
                {
                    return Task.FromResult(CopySinglePage(form, absoluteInputPath, outputDoc));
                }

                double slotW, slotH, sheetW, sheetH;
                bool isTargetA4 = targetPaperSize.Contains("A4", StringComparison.OrdinalIgnoreCase);

                if (isTargetA4)
                {
                    // A5 portrait slot dimensions on A4 Landscape sheet (297mm x 210mm)
                    slotW = 420.94; // 148.5mm in pt (A5 width)
                    slotH = 595.28; // 210mm in pt   (A5 height)
                    sheetW = 2.0 * slotW; // 841.89pt = 297mm (A4 Landscape)
                    sheetH = slotH;        // 595.28pt = 210mm
                }
                else
                {
                    // A4 portrait slot dimensions on A3 Landscape sheet (420mm x 297mm)
                    slotW = 595.28; // 210mm in pt (A4 width)
                    slotH = 841.89; // 297mm in pt (A4 height)
                    sheetW = 2.0 * slotW; // 1190.55pt = 420mm (A3 Landscape)
                    sheetH = slotH;        // 841.89pt  = 297mm
                }

                // Pad to multiple of 4 pages (blank pages fill remaining slots)
                int sheets = (inputPages + 3) / 4;
                int allPages = sheets * 4;

                logger.LogInformation("Booklet: {InputPages} source -> {Sheets} sheets ({AllPages} virtual), Target={Paper}={SheetW}x{SheetH}pt",
                    inputPages, sheets, allPages, isTargetA4 ? "A4" : "A3", sheetW, sheetH);

                for (int idx = 1; idx <= sheets; idx++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Front side: left=page from end, right=page from start
                    var frontPage = outputDoc.AddPage();
                    frontPage.Width = sheetW;
                    frontPage.Height = sheetH;

                    using (var gfx = XGraphics.FromPdfPage(frontPage))
                    {
                        DrawOnSlot(form, allPages + 2 - 2 * idx, inputPages, gfx, 0, 0, slotW, slotH);
                        DrawOnSlot(form, 2 * idx - 1, inputPages, gfx, slotW, 0, slotW, slotH);
                    }

                    // Back side: left=page from start+1, right=page from end-1
                    var backPage = outputDoc.AddPage();
                    backPage.Width = sheetW;
                    backPage.Height = sheetH;

                    using (var gfx = XGraphics.FromPdfPage(backPage))
                    {
                        DrawOnSlot(form, 2 * idx, inputPages, gfx, 0, 0, slotW, slotH);
                        DrawOnSlot(form, allPages + 1 - 2 * idx, inputPages, gfx, slotW, 0, slotW, slotH);
                    }
                }
            }

            var outputPath = Path.Combine(
                Path.GetDirectoryName(absoluteInputPath) ?? Path.GetTempPath(),
                $"booklet_{Path.GetFileNameWithoutExtension(inputPdfPath)}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf");

            outputDoc.Save(outputPath);

            logger.LogInformation("Booklet: {Pages} pages, {W}x{H}pt -> '{Path}'",
                outputDoc.PageCount, outputDoc.Pages[0].Width, outputDoc.Pages[0].Height, outputPath);

            return Task.FromResult(outputPath);
        }
        finally
        {
            outputDoc.Dispose();
        }
    }

    /// <summary>
    /// Draw a source page into a slot. Auto-rotates landscape source to fit portrait slot.
    /// Scales down if source is larger than slot, centers if smaller.
    /// </summary>
    private static void DrawOnSlot(XPdfForm form, int pageNum, int maxPages, XGraphics gfx,
        double x, double y, double slotW, double slotH)
    {
        if (pageNum < 1 || pageNum > maxPages) return;

        form.PageNumber = pageNum;

        // Source page natural dimensions
        double srcW = form.PointWidth;
        double srcH = form.PointHeight;

        // Auto-rotate: if source is landscape, swap dimensions
        double drawW, drawH;
        if (srcW > srcH)
        {
            // Source is landscape -> rotate 90 degrees to fit portrait slot
            drawW = srcH;
            drawH = srcW;
        }
        else
        {
            drawW = srcW;
            drawH = srcH;
        }

        // Scale to fit slot (maintain aspect ratio)
        double scaleX = slotW / drawW;
        double scaleY = slotH / drawH;
        double scale = Math.Min(scaleX, scaleY);

        double finalW = drawW * scale;
        double finalH = drawH * scale;

        // Center in slot
        double offsetX = x + (slotW - finalW) / 2.0;
        double offsetY = y + (slotH - finalH) / 2.0;

        gfx.DrawImage(form, new XRect(offsetX, offsetY, finalW, finalH));
    }

    private string CopySinglePage(XPdfForm form, string inputPath, PdfDocument outputDoc)
    {
        form.PageNumber = 1;
        var page = outputDoc.AddPage();
        page.Width = form.PointWidth;
        page.Height = form.PointHeight;

        using (var gfx = XGraphics.FromPdfPage(page))
        {
            gfx.DrawImage(form, new XRect(0, 0, form.PointWidth, form.PointHeight));
        }

        var outputPath = Path.Combine(
            Path.GetDirectoryName(inputPath) ?? Path.GetTempPath(),
            $"booklet_{Path.GetFileNameWithoutExtension(inputPath)}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf");

        outputDoc.Save(outputPath);
        logger.LogInformation("Single page booklet -> '{Path}'", outputPath);
        return outputPath;
    }
}
