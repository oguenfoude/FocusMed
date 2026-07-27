using System.Drawing.Printing;
using FocusMed.PrintService.Abstractions;
using FocusMed.PrintService.Configuration;
using Microsoft.Extensions.Options;

namespace FocusMed.PrintService.Services;

public sealed class PrinterCapabilityDetector
{
    private readonly IOptionsMonitor<PhysicalPrinterOptions> _options;
    private readonly ILogger<PrinterCapabilityDetector> _logger;

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

        var settings = new PrinterSettings { PrinterName = config.WindowsQueueName };
        if (!settings.IsValid)
        {
            _logger.LogWarning("Printer '{Queue}' not found or offline", config.WindowsQueueName);
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
            "Capabilities for {Printer}: Available={Available}, CanDuplex={CanDuplex}, DuplexModes=[{Modes}], PaperSizes={SizeCount}",
            config.Name, true, canDuplex, string.Join(",", duplexModes), paperSizes.Count);

        return caps;
    }
}
