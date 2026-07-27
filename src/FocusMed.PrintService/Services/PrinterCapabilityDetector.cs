using System.Drawing.Printing;
using FocusMed.PrintService.Abstractions;
using FocusMed.PrintService.Configuration;
using Microsoft.Extensions.Options;

namespace FocusMed.PrintService.Services;

public sealed class PrinterCapabilityDetector
{
    private readonly IOptionsMonitor<PhysicalPrinterOptions> _options;
    private readonly ILogger<PrinterCapabilityDetector> _logger;

    private static readonly string[] GenericSuffixes = ["Generic", "Class", "Microsoft", "Driver"];
    private static readonly string[] NativeIndicators = ["PCL", "PS", "XL", "GDI", "XPS", "PostScript"];

    public PrinterCapabilityDetector(
        IOptionsMonitor<PhysicalPrinterOptions> options,
        ILogger<PrinterCapabilityDetector> logger)
    {
        _options = options;
        _logger = logger;
    }

    public PrinterCapabilities Detect(string printerName)
    {
        var config = _options.CurrentValue.PhysicalPrinters
            .FirstOrDefault(p => string.Equals(p.Name, printerName, StringComparison.OrdinalIgnoreCase));

        if (config == null)
        {
            _logger.LogWarning("Capability query for unknown printer: {PrinterName}", printerName);
            return new PrinterCapabilities(printerName, false, false, Array.Empty<string>(), Array.Empty<PaperSizeInfo>());
        }

        var queueName = ResolveBestQueue(config);
        if (queueName == null)
        {
            _logger.LogWarning("No Windows printer found matching '{Name}'", config.Name);
            return new PrinterCapabilities(config.Name, false, false, Array.Empty<string>(), Array.Empty<PaperSizeInfo>());
        }

        var settings = new PrinterSettings { PrinterName = queueName };
        if (!settings.IsValid)
        {
            _logger.LogWarning("Printer '{Queue}' not found or offline", queueName);
            return new PrinterCapabilities(config.Name, false, false, Array.Empty<string>(), Array.Empty<PaperSizeInfo>());
        }

        var canDuplex = settings.CanDuplex;
        var duplexModes = new List<string> { "Simplex" };
        if (canDuplex)
        {
            duplexModes.Add("Vertical");
            duplexModes.Add("Horizontal");
        }

        var paperSizes = new List<PaperSizeInfo>();
        if (settings.PaperSizes != null)
        {
            foreach (PaperSize ps in settings.PaperSizes)
            {
                paperSizes.Add(new PaperSizeInfo(
                    ps.PaperName,
                    ps.Width,
                    ps.Height,
                    ps.Kind.ToString()));
            }
        }

        var caps = new PrinterCapabilities(config.Name, true, canDuplex, duplexModes, paperSizes);

        _logger.LogInformation(
            "Capabilities for {Printer} (queue={Queue}): CanDuplex={CanDuplex}, PaperSizes={SizeCount}",
            config.Name, queueName, canDuplex, paperSizes.Count);

        return caps;
    }

    /// <summary>
    /// Resolves the Windows queue name for a config entry.
    /// Uses explicit WindowsQueueName if set, otherwise auto-detects.
    /// </summary>
    public string? ResolveBestQueue(PhysicalPrinterConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.WindowsQueueName))
            return config.WindowsQueueName;

        var allPrinters = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        var candidates = allPrinters
            .Where(q => MatchesName(q, config.Name))
            .ToList();

        if (candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        return PickBestDriver(candidates);
    }

    /// <summary>
    /// Picks the best driver from candidates: prefer native over generic, then prefer CanDuplex=true.
    /// </summary>
    private string PickBestDriver(List<string> candidates)
    {
        var scored = candidates.Select(q =>
        {
            var ps = new PrinterSettings { PrinterName = q };
            var isGeneric = GenericSuffixes.Any(s =>
                q.Contains(s, StringComparison.OrdinalIgnoreCase));
            var isNative = NativeIndicators.Any(s =>
                q.Contains(s, StringComparison.OrdinalIgnoreCase));
            var canDuplex = ps.IsValid && ps.CanDuplex;

            var score = 0;
            if (isNative) score += 100;
            if (canDuplex) score += 50;
            if (!isGeneric) score += 10;

            return new { Queue = q, Score = score, CanDuplex = canDuplex, IsValid = ps.IsValid };
        })
        .Where(x => x.IsValid)
        .OrderByDescending(x => x.Score)
        .ToList();

        if (scored.Count == 0)
            return candidates[0];

        var best = scored[0];
        _logger.LogInformation(
            "Auto-detected best driver for '{Queue}': {Best} (score={Score}, CanDuplex={CanDuplex})",
            candidates[0], best.Queue, best.Score, best.CanDuplex);

        return best.Queue;
    }

    private static bool MatchesName(string queueName, string configName)
    {
        var normalized = queueName.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").ToLowerInvariant();
        var search = configName.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").ToLowerInvariant();

        return normalized.Contains(search) || search.Contains(normalized);
    }
}
