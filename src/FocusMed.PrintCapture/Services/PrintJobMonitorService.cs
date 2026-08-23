using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace FocusMed.PrintCapture.Services;

public class PrintJobMonitorService : IDisposable
{
    private readonly ILogger<PrintJobMonitorService> _logger;
    private readonly string _watchFolder;
    private readonly string _resumesFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private System.Threading.Timer? _pollTimer;
    private long _lastSeenSize = -1;
    private DateTime _lastChangeTime = DateTime.MinValue;

    public event Action<string>? OnNewResumePdf;

    public PrintJobMonitorService(ILogger<PrintJobMonitorService> logger, string watchFolder, string resumesFolder)
    {
        _logger = logger;
        _watchFolder = watchFolder;
        _resumesFolder = resumesFolder;
    }

    public void Start()
    {
        Directory.CreateDirectory(_watchFolder);

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var resumesDir = Path.Combine(dataDir, _resumesFolder);
        Directory.CreateDirectory(resumesDir);

        _pollTimer = new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(200));

        _logger.LogInformation("Polling: {Folder}\\incoming.pdf (every 200ms, stabilize 300ms)", _watchFolder);
        _logger.LogInformation("Resumes: {Folder}", resumesDir);
        _logger.LogInformation("Ready. Print any document to the FocusMed printer.");
    }

    private async Task PollAsync()
    {
        if (!await _lock.WaitAsync(0)) return;

        string? newPdfPath = null;
        try
        {
            var incomingPath = Path.Combine(_watchFolder, "incoming.pdf");

            if (!File.Exists(incomingPath))
            {
                _lastSeenSize = -1;
                return;
            }

            long currentSize;
            try
            {
                var fi = new FileInfo(incomingPath);
                currentSize = fi.Length;
            }
            catch
            {
                return;
            }

            if (currentSize == 0)
            {
                _lastSeenSize = -1;
                return;
            }

            if (currentSize == _lastSeenSize)
            {
                if (_lastSeenSize > 0 && (DateTime.UtcNow - _lastChangeTime).TotalMilliseconds >= 300)
                {
                    _lastSeenSize = -1;
                    newPdfPath = await ProcessFileAsync(incomingPath, currentSize);
                }
            }
            else
            {
                if (_lastSeenSize == -1)
                {
                    _logger.LogInformation("Print started: incoming.pdf growing ({Size:N0} bytes)...", currentSize);
                }

                _lastSeenSize = currentSize;
                _lastChangeTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll error");
        }
        finally
        {
            _lock.Release();
        }

        // Fire event AFTER releasing lock so polling isn't blocked during WPF operations
        if (newPdfPath != null)
        {
            try
            {
                OnNewResumePdf?.Invoke(newPdfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnNewResumePdf handler threw");
            }
        }
    }

    private async Task<string?> ProcessFileAsync(string incomingPath, long size)
    {
        var sw = Stopwatch.StartNew();

        var retries = 5;
        while (retries > 0)
        {
            try
            {
                using var fs = File.Open(incomingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                break;
            }
            catch (IOException)
            {
                retries--;
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException)
            {
                retries--;
                await Task.Delay(100);
            }
        }

        if (!File.Exists(incomingPath)) return null;

        var fileInfo = new FileInfo(incomingPath);
        if (fileInfo.Length == 0) return null;

        _logger.LogInformation("Print finished: incoming.pdf ({Size:N0} bytes), validating...", fileInfo.Length);

        if (!IsPdfFile(incomingPath))
        {
            _logger.LogWarning("Invalid PDF header, skipping: incoming.pdf");
            await TryZeroFileAsync(incomingPath);
            return null;
        }

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var resumesDir = Path.Combine(dataDir, _resumesFolder);
        Directory.CreateDirectory(resumesDir);

        var destFileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";
        var destPath = Path.Combine(resumesDir, destFileName);

        try
        {
            File.Copy(incomingPath, destPath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy PDF to {Path}", destPath);
            return null;
        }

        sw.Stop();
        _logger.LogInformation("Captured: {File} ({Size:N0} bytes in {Ms}ms)", destFileName, fileInfo.Length, sw.ElapsedMilliseconds);

        await TryZeroFileAsync(incomingPath);
        return destPath;
    }

    private async Task TryZeroFileAsync(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength(0);
                return;
            }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
            catch
            {
                return;
            }
        }
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _logger.LogInformation("Monitoring stopped.");
    }

    public void Dispose()
    {
        Stop();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsPdfFile(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 4) return false;
            var header = new byte[4];
            var bytesRead = fs.Read(header, 0, 4);
            if (bytesRead < 4) return false;
            return header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46;
        }
        catch
        {
            return false;
        }
    }
}
