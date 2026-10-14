using FocusMed.Printing.Imposition;
using FocusMed.Printing.Jobs;
using FocusMed.Printing.Profiles;
using Microsoft.Extensions.Options;

namespace FocusMed.Tests;

/// <summary>
/// Raw-TCP booklet honesty (D4/I): a booklet profile over a raw (driverless) preset must
/// NOT print-and-claim-success - staple/fold cannot travel over port 9100.
/// </summary>
public sealed class PrintExecutionRawBookletTests : IDisposable
{
    private readonly TestInfra _infra = new();

    private sealed class FakeBooklet : IBookletImpositionService
    {
        public Task<string> ComposeBookletAsync(string inputPdfPath, string targetPaperSize = "A3", CancellationToken ct = default)
            => Task.FromResult(inputPdfPath);
    }

    private sealed class FakeRaw : IRawPrintService
    {
        public int Calls;
        public int LastCopies = -1;
        public Task<bool> PrintPdfAsync(string printerIp, byte[] pdfData, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, int copies = 1, CancellationToken ct = default)
        {
            Calls++;
            LastCopies = copies;
            return Task.FromResult(true);
        }
        public Task<bool> PrintPdfAsync(string printerIp, string pdfFilePath, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, int copies = 1, CancellationToken ct = default)
        {
            Calls++;
            LastCopies = copies;
            return Task.FromResult(true);
        }
    }

    private static IOptions<RawPrinterConfig> RawOptions() =>
        Options.Create(new RawPrinterConfig
        {
            Printers = new List<RawPrinterPreset>
            {
                new() { Name = "RAW A3", Ip = "127.0.0.1", Port = 9100, PaperSize = "A3", WindowsPrinterName = null }
            }
        });

    private PrintExecutionService Build(FakeRaw raw) => new(
        new FakeBooklet(), raw, RawOptions(), TestInfra.NullLogger<PrintExecutionService>());

    [Fact]
    public async Task BookletOverRaw_RefusedWithoutSending()
    {
        var pdf = Path.Combine(_infra.DataDir, "in.pdf");
        await File.WriteAllBytesAsync(pdf, new byte[] { 1, 2, 3 });
        var raw = new FakeRaw();
        var svc = Build(raw);

        var result = await svc.PrintAsync(new PrintJobRequest
        {
            PrinterName = "RAW A3",
            PdfPath = pdf,
            Profile = new PrintProfile { Name = "RAW A3", IsBooklet = true, PaperSizeName = "A3", RequiresDuplex = true, UseDuplexShortEdge = true },
            Copies = 1
        });

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, raw.Calls);
    }

    [Fact]
    public async Task FlatOverRaw_ForwardsCopies()
    {
        var pdf = Path.Combine(_infra.DataDir, "flat.pdf");
        await File.WriteAllBytesAsync(pdf, new byte[] { 1, 2, 3 });
        var raw = new FakeRaw();
        var svc = Build(raw);

        var result = await svc.PrintAsync(new PrintJobRequest
        {
            PrinterName = "RAW A3",
            PdfPath = pdf,
            Profile = new PrintProfile { Name = "RAW A3", IsBooklet = false, PaperSizeName = "A3" },
            Copies = 2
        });

        Assert.True(result.Success);
        Assert.Equal(1, raw.Calls);
        Assert.Equal(2, raw.LastCopies);
    }

    public void Dispose() => _infra.Dispose();
}
