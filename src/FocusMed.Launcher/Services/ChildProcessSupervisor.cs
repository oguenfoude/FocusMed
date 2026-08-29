using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

/// <summary>
/// Supervises the Worker (DICOM listener) and Dashboard (Blazor Server) child processes.
/// Always restarts a child if it exits, with exponential backoff for crash loops.
/// One subsystem failing never takes down the launcher.
/// </summary>
public sealed class ChildProcessSupervisor : IDisposable
{
    private const int CheckIntervalMs = 500;
    private const int RestartDelayMs = 2000;
    private const int BackoffThreshold = 3;
    private const int BackoffMinMs = 10_000;
    private const int BackoffMaxMs = 30_000;

    private readonly ILogger<ChildProcessSupervisor> _logger;
    private readonly string _workerExe;
    private readonly string _dashboardExe;
    private readonly string _dataDir;
    private readonly string _dbConnection;
    private readonly int _webPort;

    private Process? _worker;
    private Process? _dashboard;
    private volatile bool _shuttingDown;
    private int _workerFastExits;
    private int _dashboardFastExits;
    private DateTime _workerLastExit = DateTime.MinValue;
    private DateTime _dashboardLastExit = DateTime.MinValue;
    private DateTime _workerMissingLogged = DateTime.MinValue;
    private DateTime _dashboardMissingLogged = DateTime.MinValue;

    /// <summary>Fired when a child's runtime status changes (for tray tooltip updates). Args: role, status text.</summary>
    public event Action<string, string>? ChildStatusChanged;

    public ChildProcessSupervisor(
        ILogger<ChildProcessSupervisor> logger,
        SiteConfig cfg)
    {
        _logger = logger;
        _dataDir = cfg.ResolvedDataDirectory;
        _dbConnection = cfg.ResolvedDbConnection;
        _webPort = cfg.WebPort;
        var baseDir = AppContext.BaseDirectory;
        _workerExe = Path.Combine(baseDir, "FocusMed.Worker.exe");
        _dashboardExe = Path.Combine(baseDir, "FocusMed.Dashboard.exe");
    }

    public Task StartAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            _logger.LogInformation("Supervisor starting...");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_shuttingDown) break;

                    _worker ??= EnsureRunning(_workerExe, "Worker", "FocusMed.Worker.exe",
                        ref _workerFastExits, ref _workerLastExit, ref _workerMissingLogged, ct);

                    _dashboard ??= EnsureRunning(_dashboardExe, "Dashboard", "FocusMed.Dashboard.exe",
                        ref _dashboardFastExits, ref _dashboardLastExit, ref _dashboardMissingLogged, ct,
                        $"ASPNETCORE_URLS=http://0.0.0.0:{_webPort}");

                    await Task.Delay(CheckIntervalMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Supervisor loop error (continuing)");
                    try { await Task.Delay(CheckIntervalMs, CancellationToken.None); } catch { }
                }
            }
            _logger.LogInformation("Supervisor loop stopped");
        }, CancellationToken.None);
    }

    private Process? EnsureRunning(
        string exePath, string role, string exeName,
        ref int fastExits, ref DateTime lastExit, ref DateTime lastMissingLogged, CancellationToken ct,
        string? extraEnv = null)
    {
        if (!File.Exists(exePath))
        {
            if (DateTime.UtcNow - lastMissingLogged > TimeSpan.FromSeconds(30))
            {
                _logger.LogCritical("Missing child executable: {Exe}", exePath);
                SetStatus(role, "Missing");
                lastMissingLogged = DateTime.UtcNow;
            }
            return null;
        }

        var delay = ComputeDelay(ref fastExits, ref lastExit);
        if (delay > TimeSpan.Zero)
        {
            _logger.LogInformation("{Role} backoff: waiting {Delay}s before restart", role, delay.TotalSeconds);
            SetStatus(role, $"Restarting (backoff {delay.TotalSeconds:0}s)");
            try { Task.Delay(delay, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return null; }
            if (ct.IsCancellationRequested) return null;
        }

        try
        {
            lastExit = DateTime.UtcNow;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            };
            psi.Environment["FOCUSMED_DATA"] = _dataDir;
            psi.Environment["FOCUSMED_DB_CONNECTION"] = _dbConnection;
            if (!string.IsNullOrWhiteSpace(extraEnv))
            {
                var idx = extraEnv.IndexOf('=');
                if (idx > 0)
                    psi.Environment[extraEnv[..idx]] = extraEnv[(idx + 1)..];
            }

            var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogCritical("Failed to start {Role} (Process.Start returned null)", role);
                SetStatus(role, "Start failed");
                return null;
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogInformation("[{Role}] {Line}", role, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _logger.LogWarning("[{Role}] {Line}", role, e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            SetStatus(role, "Running");

            _ = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Role} WaitForExit failed", role);
                    return;
                }

                if (_shuttingDown)
                {
                    _logger.LogInformation("{Role} exited during shutdown", role);
                    return;
                }

                var code = process.ExitCode;
                _logger.LogWarning("{Role} exited ({RoleExe}) with code {Code}; scheduling restart", role, exeName, code);
                SetStatus(role, $"Restarting ({code})");

                if (role == "Worker")
                {
                    if (ReferenceEquals(process, _worker)) _worker = null;
                }
                else if (role == "Dashboard")
                {
                    if (ReferenceEquals(process, _dashboard)) _dashboard = null;
                }
            }, CancellationToken.None);

            _logger.LogInformation("{Role} started: {Exe}", role, exePath);
            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {Role} ({Exe})", role, exePath);
            SetStatus(role, "Start failed");
            return null;
        }
    }

    private static TimeSpan ComputeDelay(ref int fastExits, ref DateTime lastExit)
    {
        // First start: no backoff.
        if (lastExit == DateTime.MinValue) return TimeSpan.Zero;

        var uptime = DateTime.UtcNow - lastExit;
        if (uptime < TimeSpan.FromSeconds(5))
        {
            fastExits++;
        }
        else
        {
            fastExits = 0;
        }

        if (fastExits < BackoffThreshold) return TimeSpan.FromMilliseconds(RestartDelayMs);

        var backoffMs = Math.Min(BackoffMinMs * (double)(fastExits - BackoffThreshold + 1), BackoffMaxMs);
        return TimeSpan.FromMilliseconds(backoffMs);
    }

    private void SetStatus(string role, string status)
    {
        try { ChildStatusChanged?.Invoke(role, status); }
        catch (Exception ex) { _logger.LogWarning(ex, "ChildStatusChanged handler threw"); }
    }

    public string GetStatusText()
    {
        var worker = _worker == null || _worker.HasExited ? "Stopped" : "Running";
        var dash = _dashboard == null || _dashboard.HasExited ? "Stopped" : "Running";

        if (!File.Exists(_workerExe)) worker = "Missing";
        if (!File.Exists(_dashboardExe)) dash = "Missing";

        // NotifyIcon.Text is capped at 63 chars — keep it short.
        return $"FocusMed: Worker {worker} | Dashboard {dash}";
    }

    public async Task StopAsync(TimeSpan grace)
    {
        _shuttingDown = true;
        _logger.LogInformation("Stopping child processes...");

        await StopProcessAsync(_worker, "Worker");
        await StopProcessAsync(_dashboard, "Dashboard");

        // Give the loop a moment to observe the shutdown flag and exit.
        try { await Task.Delay(CheckIntervalMs + 100, new CancellationTokenSource(grace).Token); }
        catch { }
    }

    private async Task StopProcessAsync(Process? process, string role)
    {
        if (process == null) return;
        if (process.HasExited)
        {
            _logger.LogInformation("{Role} already exited", role);
            return;
        }

        _logger.LogInformation("Stopping {Role} (graceful then force)...", role);
        try
        {
            process.CloseMainWindow();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Role} CloseMainWindow failed; falling back to Kill", role);
        }

        if (!process.HasExited)
        {
            try
            {
                _logger.LogWarning("{Role} did not exit gracefully; killing process tree", role);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill {Role} process tree", role);
            }
        }

        try { process.Dispose(); } catch { }
    }

    public void Dispose()
    {
        try { StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor dispose stop failed"); }
    }
}
