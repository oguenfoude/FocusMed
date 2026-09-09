using FocusMed.Dashboard.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FocusMed.Tests;

/// <summary>
/// Cover generation: GeneratePrintPdf with a real PNG but WITHOUT any cover.docx
/// present. Proves the QuestPDF-only path renders a valid multi-page PDF
/// (cover + images) with no Word/COM dependency.
/// </summary>
public sealed class CoverPdfTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private string WriteTestPng()
    {
        var path = Path.Combine(_infra.DataDir, "test-image.png");
        using var img = new Image<Rgba32>(20, 20);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(200, 30, 30);
            }
        });
        img.SaveAsPng(path);
        return path;
    }

    [Fact]
    public async Task GeneratePrintPdf_WithoutCoverDocx_ProducesCoverPlusImages()
    {
        Assert.False(File.Exists(Path.Combine(_infra.DataDir, "cover.docx")));
        var png = WriteTestPng();
        var svc = new PdfService(TestInfra.NullLogger<PdfService>());

        var url = await svc.GeneratePrintPdf("TEST^PATIENT", "08/09/2026", "Test study",
            new[] { png }, resumePdfPath: null, pageSize: "A4");

        Assert.StartsWith("/pdf-cache/", url);
        var file = Path.Combine(_infra.DataDir, "pdf-cache", Path.GetFileName(url));
        Assert.True(File.Exists(file));
        Assert.True(new FileInfo(file).Length > 1000);
        Assert.True(svc.GetPageCount(url) >= 2, "expected cover page + at least one image page");
    }

    [Fact]
    public async Task GeneratePrintPdf_SameInputs_ReturnsCachedUrl()
    {
        var png = WriteTestPng();
        var svc = new PdfService(TestInfra.NullLogger<PdfService>());

        var first = await svc.GeneratePrintPdf("CACHE^TEST", "08/09/2026", "", new[] { png });
        var second = await svc.GeneratePrintPdf("CACHE^TEST", "08/09/2026", "", new[] { png });

        Assert.Equal(first, second);
    }

    public void Dispose() => _infra.Dispose();
}
