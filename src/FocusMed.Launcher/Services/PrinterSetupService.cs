using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

public class PrinterSetupService
{
    private readonly ILogger<PrinterSetupService> _logger;
    private readonly string _printerName;
    private readonly string _driverName;
    private readonly string _printJobsFolder;

    public PrinterSetupService(ILogger<PrinterSetupService> logger, string printerName, string driverName, string printJobsFolder)
    {
        _logger = logger;
        _printerName = printerName;
        _driverName = driverName;
        _printJobsFolder = printJobsFolder;
    }

    public async Task EnsurePrinterExistsAsync()
    {
        Directory.CreateDirectory(_printJobsFolder);

        var incomingPdfPath = Path.Combine(_printJobsFolder, "incoming.pdf");
        var portName = incomingPdfPath;

        if (IsPrinterInstalled())
        {
            var currentPort = GetPrinterPort();
            if (!string.Equals(currentPort, portName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Fixing printer port: {Old} → {New}", currentPort, portName);
                await SetPrinterPortAsync(portName);
            }
            else
            {
                _logger.LogInformation("Printer '{PrinterName}' already installed on correct port", _printerName);
            }
            return;
        }

        try
        {
            _logger.LogInformation("Installing printer '{PrinterName}' with driver '{DriverName}' on Local Port...", _printerName, _driverName);
            await InstallPrinterAsync(portName);
            if (!IsPrinterInstalled())
            {
                _logger.LogWarning("Printer '{PrinterName}' reported install success but is not present (needs Admin / check event log). Capture will not work until installed.", _printerName);
            }
            else
            {
                _logger.LogInformation("Printer '{PrinterName}' installed successfully", _printerName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not install printer '{PrinterName}'. The app will still monitor the print-jobs folder.", _printerName);
        }
    }

    // PowerShell single-quote escaping: names/paths are embedded in '...' literals,
    // so a literal ' would break out of the string (or inject). Doubling it is the
    // PowerShell escape.
    private static string EscapePs(string value) => value.Replace("'", "''");

    private bool IsPrinterInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command \"Get-Printer -Name '{EscapePs(_printerName)}' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return false;

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return !string.IsNullOrEmpty(output);
        }
        catch
        {
            return false;
        }
    }

    private string GetPrinterPort()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-Command \"(Get-Printer -Name '{EscapePs(_printerName)}').PortName\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return "";

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                return "";
            }
            return output;
        }
        catch
        {
            return "";
        }
    }

    private async Task SetPrinterPortAsync(string portName)
    {
        var script = $"Set-Printer -Name '{EscapePs(_printerName)}' -PortName '{EscapePs(portName)}' -ErrorAction SilentlyContinue";
        await RunPowerShell(script);
    }

    private async Task InstallPrinterAsync(string portName)
    {
        var installScript = $@"
            Add-PrinterPort -Name '{EscapePs(portName)}' -ErrorAction SilentlyContinue
            Add-Printer -Name '{EscapePs(_printerName)}' -DriverName '{EscapePs(_driverName)}' -PortName '{EscapePs(portName)}' -ErrorAction SilentlyContinue
        ";

        _logger.LogInformation("Running printer install script...");
        await RunPowerShell(installScript);
        _logger.LogInformation("Printer '{PrinterName}' created on Local Port: {Port}", _printerName, portName);
    }

    private async Task RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return;

        // Drain BOTH streams concurrently: sequential ReadToEndAsync(stdout) then
        // ReadToEndAsync(stderr) can deadlock when the child fills the stderr pipe
        // while we are still blocked reading stdout.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _logger.LogDebug("PowerShell output: {Output}", stdout);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            if (!stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("PowerShell warning: {Error}", stderr);
        }
    }
}
