using FocusMed.Dashboard.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FocusMed.Tests;

/// <summary>
/// Merged-PDF cache honesty (D1/F + P4): the cache key carries the resume file's
/// existence+length+mtime, so a replaced resume under the same path never serves a
/// stale merge; cache hits touch mtime so in-use previews survive TTL sweeps.
/// </summary>
public sealed class PdfCacheFingerprintTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private string WriteTestPng(string name)
    {
        var path = Path.Combine(_infra.DataDir, name);
        using var img = new Image<Rgba32>(20, 20);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(30, 200, 30);
            }
        });
        img.SaveAsPng(path);
        return path;
    }

    [Fact]
    public async Task SameInputs_TouchKeepsFileAlive()
    {
        var png = WriteTestPng("fp.png");
        var svc = new PdfService(TestInfra.NullLogger<PdfService>());

        var first = await svc.GeneratePrintPdf("FP^TEST", "08/09/2026", "", new[] { png });
        var file = Path.Combine(_infra.DataDir, "pdf-cache", Path.GetFileName(first));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));

        var second = await svc.GeneratePrintPdf("FP^TEST", "08/09/2026", "", new[] { png });
        Assert.Equal(first, second);
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task MissingResumeVsNoResume_DifferentKeys()
    {
        var png = WriteTestPng("fp2.png");
        var svc = new PdfService(TestInfra.NullLogger<PdfService>());

        var noResume = await svc.GeneratePrintPdf("FP2^TEST", "08/09/2026", "", new[] { png }, resumePdfPath: null);
        var missingResume = await svc.GeneratePrintPdf("FP2^TEST", "08/09/2026", "", new[] { png }, resumePdfPath: "resumes/gone.pdf");

        Assert.NotEqual(noResume, missingResume);
        Assert.True(File.Exists(Path.Combine(_infra.DataDir, "pdf-cache", Path.GetFileName(noResume))));
        Assert.True(File.Exists(Path.Combine(_infra.DataDir, "pdf-cache", Path.GetFileName(missingResume))));
    }

    public void Dispose() => _infra.Dispose();
}
