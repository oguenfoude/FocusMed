using System.Drawing.Printing;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Discovery;

internal sealed class LegacyCapabilityProvider(ILogger<LegacyCapabilityProvider> logger)
{
    public PrinterCapabilitySnapshot? TryGet(string printerName)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printerName };

            if (!settings.IsValid)
            {
                logger.LogDebug("LegacyCapabilityProvider: Printer '{PrinterName}' not found or invalid", printerName);
                return null;
            }

            var paperSizes = settings.PaperSizes
                .Cast<PaperSize>()
                .Select(ps => new PaperSizeInfo
                {
                    Name = ps.PaperName,
                    WidthMm = ps.Width / 100f * 25.4f,
                    HeightMm = ps.Height / 100f * 25.4f,
                    PaperKindId = (int)ps.Kind
                })
                .ToList();

            var paperTrays = settings.PaperSources
                .Cast<PaperSource>()
                .Select(src => new PaperTrayInfo
                {
                    Name = src.SourceName,
                    BinNumber = src.RawKind
                })
                .ToList();

            var resolutions = settings.PrinterResolutions
                .Cast<PrinterResolution>()
                .Where(r => r.X > 0 && r.Y > 0) // Skip Draft/Low/Medium/High (return negative enum values)
                .Select(r => new ResolutionInfo
                {
                    DpiX = r.X,
                    DpiY = r.Y,
                    IsDefault = r.Kind == PrinterResolutionKind.Medium
                })
                .ToList();

            var snapshot = new PrinterCapabilitySnapshot
            {
                PrinterName = printerName,
                DriverName = "System.Drawing.Printing",
                SupportsDuplex = settings.CanDuplex,
                SupportsColor = settings.SupportsColor,
                SupportsCollation = false, // GDI+ does not expose collation capability
                PaperSizes = paperSizes,
                PaperTrays = paperTrays,
                Resolutions = resolutions,
                DiscoverySource = "System.Drawing.Printing",
                PaperToTrayMap = new Dictionary<string, int>() // Win32 DEVMODE probing provides this
            };

            logger.LogInformation("LegacyCapabilityProvider: Found {PaperCount} paper sizes, {TrayCount} trays, Duplex={HasDuplex}, Color={HasColor} for '{PrinterName}'",
                paperSizes.Count, paperTrays.Count, settings.CanDuplex, settings.SupportsColor, printerName);

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LegacyCapabilityProvider: Failed to query '{PrinterName}'", printerName);
            return null;
        }
    }
}
