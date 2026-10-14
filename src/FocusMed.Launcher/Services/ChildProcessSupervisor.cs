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

    private volatile Process? _worker;
    private volatile Process? _dashboard;
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

    // Per-child supervision tasks: Worker and Dashboard each get their own loop so a
    // crash-loop backoff (up to 30s) on one child can never starve supervision of the
    // other. The old single loop blocked inside the backoff delay; a fast-crashing
    // Worker stalled Dashboard restarts for the whole backoff window.
    public Task StartAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            _logger.LogInformation("Supervisor starting...");
            var workerLoop = SuperviseChildAsync(
                _workerExe, "Worker", "FocusMed.Worker.exe",
                () => _worker, p => _worker = p,
                () => _workerFastExits, v => _workerFastExits = v,
                () => _workerLastExit, v => _workerLastExit = v,
                () => _workerMissingLogged, v => _workerMissingLogged = v,
                extraEnv: null, ct: ct);
            // Bind BOTH IPv4 (0.0.0.0) and IPv6 ([::]) so localhost (::1), 127.0.0.1,
            // and the LAN IP all reach the Blazor Server SignalR circuit. A single
            // 0.0.0.0 (IPv4-only) binding leaves Chrome's localhost (IPv6) circuit dead.
            var dashboardLoop = SuperviseChildAsync(
                _dashboardExe, "Dashboard", "FocusMed.Dashboard.exe",
                () => _dashboard, p => _dashboard = p,
                () => _dashboardFastExits, v => _dashboardFastExits = v,
                () => _dashboardLastExit, v => _dashboardLastExit = v,
                () => _dashboardMissingLogged, v => _dashboardMissingLogged = v,
                extraEnv: $"ASPNETCORE_URLS=http://0.0.0.0:{_webPort};http://[::]:{_webPort}", ct: ct);
            try
            {
                await Task.WhenAll(workerLoop, dashboardLoop);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Supervisor loops cancelled during shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Supervisor loop error (app continues)");
            }
            _logger.LogInformation("Supervisor loop stopped");
        }, CancellationToken.None);
    }

    private async Task SuperviseChildAsync(
        string exePath, string role, string exeName,
        Func<Process?> getCurrent, Action<Process?> setCurrent,
        Func<int> getFastExits, Action<int> setFastExits,
        Func<DateTime> getLastExit, Action<DateTime> setLastExit,
        Func<DateTime> getMissingLogged, Action<DateTime> setMissingLogged,
        string? extraEnv, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_shuttingDown)
        {
            try
            {
                if (getCurrent() == null)
                {
                    var started = await EnsureRunningAsync(
                        exePath, role, exeName,
                        getFastExits, setFastExits, getLastExit, setLastExit,
                        getMissingLogged, setMissingLogged, extraEnv, ct);
                    setCurrent(started);
                }
                await Task.Delay(CheckIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Role} supervisor loop error (continuing)", role);
                try { await Task.Delay(CheckIntervalMs, CancellationToken.None); } catch { }
            }
        }
    }

    private async Task<Process?> EnsureRunningAsync(
        string exePath, string role, string exeName,
        Func<int> getFastExits, Action<int> setFastExits,
        Func<DateTime> getLastExit, Action<DateTime> setLastExit,
        Func<DateTime> getMissingLogged, Action<DateTime> setMissingLogged,
        string? extraEnv, CancellationToken ct)
    {
        if (!File.Exists(exePath))
        {
            if (DateTime.UtcNow - getMissingLogged() > TimeSpan.FromSeconds(30))
            {
                _logger.LogCritical("Missing child executable: {Exe}", exePath);
                SetStatus(role, "Missing");
                setMissingLogged(DateTime.UtcNow);
            }
            return null;
        }

        // Crash-loop accounting is pure here (per-child fields via accessors): the
        // count updates first, then the delay derives from the new count.
        var fastExits = NextFastExits(getFastExits(), getLastExit());
        setFastExits(fastExits);
        var delay = ComputeDelay(fastExits, getLastExit());
        if (delay > TimeSpan.Zero)
        {
            _logger.LogInformation("{Role} backoff: waiting {Delay}s before restart", role, delay.TotalSeconds);
            SetStatus(role, $"Restarting (backoff {delay.TotalSeconds:0}s)");
            // Cancellable shutdown: the real token flows through the backoff delay so
            // StopAsync never waits out a 30s backoff.
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { _logger.LogDebug("{Role} backoff cancelled during shutdown", role); return null; }
            if (ct.IsCancellationRequested) return null;
        }

        try
        {
            setLastExit(DateTime.UtcNow);
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
                    _logger.LogDebug("{Role} WaitForExit cancelled (shutdown)", role);
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
                process.Dispose();
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

    private static int NextFastExits(int fastExits, DateTime lastExit)
    {
        // First start: no history yet.
        if (lastExit == DateTime.MinValue) return fastExits;
        var uptime = DateTime.UtcNow - lastExit;
        return uptime < TimeSpan.FromSeconds(5) ? fastExits + 1 : 0;
    }

    private static TimeSpan ComputeDelay(int fastExits, DateTime lastExit)
    {
        // First start: no backoff.
        if (lastExit == DateTime.MinValue) return TimeSpan.Zero;

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
        using var cts = new CancellationTokenSource(grace);
        try { await Task.Delay(CheckIntervalMs + 100, cts.Token); }
        catch (OperationCanceledException) { _logger.LogDebug("Supervisor loop delay cancelled during shutdown"); }
        catch (Exception ex) { _logger.LogDebug(ex, "Supervisor loop delay error"); }
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
            catch (OperationCanceledException) { _logger.LogDebug("{Role} CloseMainWindow wait cancelled", role); }
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

        try { process.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose {Role} process", role); }
    }

    public void Dispose()
    {
        try { StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Supervisor dispose stop failed"); }
    }
}
