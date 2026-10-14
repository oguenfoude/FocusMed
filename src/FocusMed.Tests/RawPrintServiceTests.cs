using FocusMed.Printing.Jobs;

namespace FocusMed.Tests;

/// <summary>
/// Raw-TCP honesty (D4/I): copies travel on the raw path (PJL QTY + repeated bodies),
/// never silently dropped to 1.
/// </summary>
public sealed class RawPrintServiceTests : IDisposable
{
    private readonly TestInfra _infra = new();

    [Fact]
    public async Task PrintPdf_NoIp_ReturnsFalseWithoutSending()
    {
        var svc = new RawPrintService(TestInfra.NullLogger<RawPrintService>());
        Assert.False(await svc.PrintPdfAsync("", new byte[] { 1, 2, 3 }));
        Assert.False(await svc.PrintPdfAsync("   ", new byte[] { 1, 2, 3 }, copies: 3));
    }

    [Fact]
    public async Task PrintPdf_MissingFile_ReturnsFalse()
    {
        var svc = new RawPrintService(TestInfra.NullLogger<RawPrintService>());
        Assert.False(await svc.PrintPdfAsync("127.0.0.1",
            Path.Combine(_infra.DataDir, "nope.pdf"), copies: 2));
    }

    [Fact]
    public void BuildPjlHeader_SingleCopy_HasNoQty()
    {
        var header = System.Text.Encoding.ASCII.GetString(
            RawPrintService.BuildPjlHeader("A3", duplex: true, shortEdgeBind: true));
        Assert.Contains("DUPLEX", header);
        Assert.DoesNotContain("QTY", header);
    }

    [Fact]
    public void BuildPjlHeader_MultiCopy_SetsQty()
    {
        var header = System.Text.Encoding.ASCII.GetString(
            RawPrintService.BuildPjlHeader("A4", duplex: false, shortEdgeBind: false, copies: 3));
        Assert.Contains("@PJL SET QTY=3", header);
    }

    public void Dispose() => _infra.Dispose();
}
