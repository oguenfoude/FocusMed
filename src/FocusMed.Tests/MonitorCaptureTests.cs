using FocusMed.Launcher.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FocusMed.Tests;

/// <summary>
/// Print-capture loop without the printer driver or WPF: PrintJobMonitorService
/// watches a temp folder, validates + copies the PDF, and fires OnNewResumePdf.
/// Proves detection, stabilization, validation, and the event contract the
/// ResumePickerWindow relies on.
/// </summary>
public sealed class MonitorCaptureTests : IDisposable
{
    private readonly TestInfra _infra = new();
    private readonly string _watchDir;
    private readonly string _resumesDir;

    public MonitorCaptureTests()
    {
        _watchDir = Path.Combine(_infra.DataDir, "prints");
        _resumesDir = Path.Combine(_infra.DataDir, "resumes");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_resumesDir);
    }

    private static byte[] MinimalPdf()
    {
        var body = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
                   "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
                   "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]>>endobj\n" +
                   "trailer<</Root 1 0 R>>\n%%EOF";
        return System.Text.Encoding.ASCII.GetBytes(body);
    }

    private PrintJobMonitorService BuildService() => new(
        TestInfra.NullLogger<PrintJobMonitorService>(), _watchDir, _resumesDir);

    [Fact]
    public async Task DropPdf_CopiedToResumesAndEventFires()
    {
        var svc = BuildService();
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnNewResumePdf += path => fired.TrySetResult(path);
        svc.Start();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(_watchDir, "incoming.pdf"), MinimalPdf());

            var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(fired.Task, completed);
            var captured = await fired.Task;
            Assert.True(File.Exists(captured), "captured copy must exist");
            Assert.StartsWith(_resumesDir, captured);
        }
        finally
        {
            svc.Stop();
        }
    }

    [Fact]
    public async Task DropGarbage_NoEventFires()
    {
        var svc = BuildService();
        var fired = false;
        svc.OnNewResumePdf += _ => fired = true;
        svc.Start();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(_watchDir, "incoming.pdf"),
                System.Text.Encoding.ASCII.GetBytes("not a pdf at all"));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(fired);
        }
        finally
        {
            svc.Stop();
        }
    }

    public void Dispose() => _infra.Dispose();
}
