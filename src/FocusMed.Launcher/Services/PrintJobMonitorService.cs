using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

public class PrintJobMonitorService : IDisposable
{
    private readonly ILogger<PrintJobMonitorService> _logger;
    private readonly string _watchFolder;
    private readonly string _resumesFolder;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private System.Threading.Timer? _pollTimer;
    private long _lastSeenSize = -1;
    private DateTime _lastChangeTime = DateTime.MinValue;
    // Settled-content memory: (size + mtime) of the last fully handled file. Prevents
    // duplicate popups when zeroing fails AND prevents re-firing on an invalid file
    // that never gets removed. A new print always changes size and/or mtime.
    private long _settledSize = -1;
    private DateTime _settledMtime = DateTime.MinValue;

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
        _logger.LogInformation("Note: a leftover incoming.pdf from a previous run (orphan) will be captured as a new print on startup.");
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
            DateTime currentMtime;
            try
            {
                var fi = new FileInfo(incomingPath);
                currentSize = fi.Length;
                currentMtime = fi.LastWriteTimeUtc;
            }
            catch
            {
                return;
            }

            if (currentSize == 0)
            {
                _lastSeenSize = -1;
                _settledSize = -1;
                return;
            }

            // Already handled this exact content (capture done but zeroing failed, or
            // invalid file left in place): wait for new data instead of re-firing.
            if (currentSize == _settledSize && currentMtime == _settledMtime)
                return;

            if (currentSize == _lastSeenSize)
            {
                if (_lastSeenSize > 0 && (DateTime.UtcNow - _lastChangeTime).TotalMilliseconds >= 300)
                {
                    var (captured, settled) = await TryCaptureAsync(incomingPath, currentSize);
                    newPdfPath = captured;
                    if (settled)
                    {
                        _settledSize = currentSize;
                        _settledMtime = currentMtime;
                        _lastSeenSize = -1;
                    }
                    else
                    {
                        // Transient (locked copy): back off another stabilization window
                        // instead of busy-retrying every 200ms. Never destroys data.
                        _lastChangeTime = DateTime.UtcNow;
                    }
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

    // Returns (capturedPath, settled). settled=false means transient (try again later);
    // settled=true means the content was fully handled — valid (fired) or invalid
    // (skipped). Incomplete/invalid files are NEVER zeroed or destroyed (wait-or-skip):
    // the spooler may still be writing, and deleting evidence hides print failures.
    private async Task<(string? Captured, bool Settled)> TryCaptureAsync(string incomingPath, long size)
    {
        var sw = Stopwatch.StartNew();

        // Single-file rendezvous: snapshot the spooler file into a per-job temp first,
        // so a concurrent spooler write cannot tear the bytes we validate and copy.
        var rendezvousPath = incomingPath + $".{Guid.NewGuid():N}.tmp";
        var copied = false;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.Copy(incomingPath, rendezvousPath, overwrite: false);
                copied = true;
                break;
            }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Rendezvous copy failed (non-fatal, will retry)");
                return (null, false);
            }
        }
        if (!copied) return (null, false);

        try
        {
            if (!IsCompletePdfFile(rendezvousPath))
            {
                _logger.LogWarning("Invalid or truncated PDF (bad header or missing %%EOF); leaving incoming.pdf untouched, skipping until new data arrives.");
                return (null, true);
            }

            var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var resumesDir = Path.Combine(dataDir, _resumesFolder);
            Directory.CreateDirectory(resumesDir);

            var destFileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.pdf";
            var destPath = Path.Combine(resumesDir, destFileName);

            try
            {
                File.Copy(rendezvousPath, destPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy PDF to {Path}", destPath);
                return (null, false);
            }

            sw.Stop();
            _logger.LogInformation("Captured: {File} ({Size:N0} bytes in {Ms}ms)", destFileName, size, sw.ElapsedMilliseconds);

            if (!await TryZeroFileAsync(incomingPath))
            {
                // Zero-fail: keep the settled marker (set by the caller) so this exact
                // content never fires a duplicate popup. The next print changes
                // size/mtime and is detected normally.
                _logger.LogError("Captured {File} but could not reset incoming.pdf (locked); it will be skipped until new data arrives.", destFileName);
            }
            return (destPath, true);
        }
        finally
        {
            try { if (File.Exists(rendezvousPath)) File.Delete(rendezvousPath); } catch { }
        }
    }

    private async Task<bool> TryZeroFileAsync(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength(0);
                return true;
            }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unexpected error zeroing file (non-fatal)");
                return false;
            }
        }
        return false;
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

    private static bool IsCompletePdfFile(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 8) return false;

            var header = new byte[4];
            if (fs.Read(header, 0, 4) < 4) return false;
            if (header[0] != 0x25 || header[1] != 0x50 || header[2] != 0x44 || header[3] != 0x46)
                return false;

            // The spooler writes the %PDF header first; the trailer is written last.
            // Requiring %%EOF means we only copy files the spooler fully flushed.
            var tailLen = (int)Math.Min(fs.Length, 1024);
            var tail = new byte[tailLen];
            fs.Position = fs.Length - tailLen;
            if (fs.Read(tail, 0, tailLen) < tailLen) return false;
            var tailText = System.Text.Encoding.ASCII.GetString(tail);
            return tailText.Contains("%%EOF", StringComparison.Ordinal);
        }
            catch
            {
                return false;
            }
    }
}
