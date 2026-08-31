using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FocusMed.Launcher.Services;

/// <summary>
/// Never-fail site configuration. Loaded from {appDir}\config.json (if present) with
/// typed defaults for every field. Any parse/IO error backs up the corrupt file and
/// regenerates defaults — Load NEVER throws.
/// </summary>
public class SiteConfig
{
    public string AETitle { get; set; } = "FOCUSMED";
    public int DicomPort { get; set; } = 11112;
    public int WebPort { get; set; } = 5000;
    public string DataDirectory { get; set; } = "";
    public string RawPrinterIp { get; set; } = "192.168.1.160";
    public int RawPrinterPort { get; set; } = 9100;
    public string KonicaWindowsPrinterName { get; set; } = "KONICA MINOLTA bizhub C250i PCL (192.168.1.160) v4";
    public string VirtualPrinterName { get; set; } = "FocusMed";
    public string OutputDriverName { get; set; } = "Microsoft Print To PDF";
    public string PrintJobsFolder { get; set; } = "C:\\FocusMed_Prints";
    public string ResumesFolder { get; set; } = "resumes";
    public bool AutostartEnabled { get; set; } = true;
    public bool AutoOpenDashboardOnStart { get; set; } = false;

    /// <summary>Detected LAN IP used to open the Dashboard (confirmed reliable on client PCs).</summary>
    public string LocalIp { get; set; } = "";

    /// <summary>Data directory: explicit override or %LOCALAPPDATA%\FocusMed.</summary>
    public string ResolvedDataDirectory =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed")
            : DataDirectory;

    /// <summary>SQLite connection string in the resolved data directory.</summary>
    public string ResolvedDbConnection => $"Data Source={Path.Combine(ResolvedDataDirectory, "focusmed.db")}";

    public static SiteConfig Load(ILogger logger, string appDir)
    {
        var configPath = Path.Combine(appDir, "config.json");
        var defaults = new SiteConfig();

        if (!File.Exists(configPath))
        {
            try
            {
                defaults.DetectAndApplyAutoSettings(logger);
                WriteDefaults(configPath, defaults);
                logger.LogInformation("Created auto-detected config: {ConfigPath}", configPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write default config {ConfigPath}", configPath);
            }
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize<SiteConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (loaded == null)
            {
                logger.LogWarning("Config {ConfigPath} parsed to null; regenerating defaults", configPath);
                return RecoverCorruptConfig(logger, appDir, configPath, defaults);
            }

            ForceJsonBackedProperties(logger, defaults, loaded);

            if (string.IsNullOrWhiteSpace(loaded.RawPrinterIp) || loaded.RawPrinterIp == "192.168.1.160")
            {
                logger.LogInformation("Config has default printer IP; attempting auto-detection");
                loaded.DetectAndApplyAutoSettings(logger);
                try { WriteDefaults(configPath, loaded); } catch { }
            }

            return loaded;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load config {ConfigPath}; backing up and regenerating defaults", configPath);
            return RecoverCorruptConfig(logger, appDir, configPath, defaults);
        }
    }

    public void DetectAndApplyAutoSettings(ILogger logger)
    {
        try
        {
            var ip = DetectLocalIp();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                LocalIp = ip;
                logger.LogInformation("Auto-detected local IP: {Ip}", ip);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IP auto-detection failed");
        }

        try
        {
            var (printerName, printerIp) = DetectKonicaPrinter();
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                KonicaWindowsPrinterName = printerName;
                logger.LogInformation("Auto-detected Konica printer: {Name}", printerName);
            }
            if (!string.IsNullOrWhiteSpace(printerIp))
            {
                RawPrinterIp = printerIp;
                logger.LogInformation("Auto-detected printer IP: {Ip}", printerIp);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Printer auto-detection failed");
        }
    }

    private static string DetectLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (ip != null) return ip.ToString();
        }
        catch { }

        return "";
    }

    private static (string PrinterName, string PrinterIp) DetectKonicaPrinter()
    {
        try
        {
            foreach (string name in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            {
                if (name.Contains("KONICA", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Minolta", StringComparison.OrdinalIgnoreCase))
                {
                    var ip = ExtractIpFromPrinterName(name);
                    return (name, ip);
                }
            }
        }
        catch { }

        return ("", "");
    }

    private static string ExtractIpFromPrinterName(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static SiteConfig RecoverCorruptConfig(ILogger logger, string appDir, string configPath, SiteConfig defaults)
    {
        try
        {
            var backup = Path.Combine(appDir, $"config.errored-{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Move(configPath, backup, overwrite: true);
            logger.LogWarning("Backed up corrupt config to {Backup}", backup);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not back up corrupt config {ConfigPath}", configPath);
        }

        try
        {
            WriteDefaults(configPath, defaults);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write regenerated default config {ConfigPath}", configPath);
        }
        return defaults;
    }

    private static void WriteDefaults(string configPath, SiteConfig defaults)
    {
        var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    /// <summary>
    /// JSON that is absent keeps its POCO initializer (the default). An explicit JSON null
    /// would overwrite that with null, so a minimal presence healthy check is applied to the
    /// fields that must never be empty. System.Text.Json does not itself fill absent members.
    /// </summary>
    private static void ForceJsonBackedProperties(ILogger logger, SiteConfig defaults, SiteConfig loaded)
    {
        if (string.IsNullOrWhiteSpace(loaded.AETitle))
        {
            logger.LogWarning("config.json has empty AETitle; reverting to default");
            loaded.AETitle = defaults.AETitle;
        }
        if (string.IsNullOrWhiteSpace(loaded.DataDirectory)) loaded.DataDirectory = defaults.DataDirectory;
        if (string.IsNullOrWhiteSpace(loaded.RawPrinterIp)) loaded.RawPrinterIp = defaults.RawPrinterIp;
        if (string.IsNullOrWhiteSpace(loaded.VirtualPrinterName)) loaded.VirtualPrinterName = defaults.VirtualPrinterName;
        if (string.IsNullOrWhiteSpace(loaded.OutputDriverName)) loaded.OutputDriverName = defaults.OutputDriverName;
        if (string.IsNullOrWhiteSpace(loaded.PrintJobsFolder)) loaded.PrintJobsFolder = defaults.PrintJobsFolder;
        if (string.IsNullOrWhiteSpace(loaded.ResumesFolder)) loaded.ResumesFolder = defaults.ResumesFolder;
    }
}
