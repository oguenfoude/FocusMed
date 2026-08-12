using System.Printing;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Discovery;

internal sealed class ModernCapabilityProvider(ILogger<ModernCapabilityProvider> logger)
{
    public PrinterCapabilitySnapshot? TryGet(string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            var queue = server.GetPrintQueue(printerName);
            if (queue is null)
            {
                logger.LogDebug("ModernCapabilityProvider: PrintQueue '{PrinterName}' not found", printerName);
                return null;
            }

            var capabilitiesXml = queue.GetPrintCapabilitiesAsXml();
            if (capabilitiesXml is null)
            {
                logger.LogDebug("ModernCapabilityProvider: GetPrintCapabilitiesAsXml returned null for '{PrinterName}'", printerName);
                return null;
            }

            var doc = XDocument.Load(capabilitiesXml);
            var root = doc.Root;
            if (root is null) return null;

            var ns = root.Name.Namespace;

            bool supportsDuplex = root.Descendants(ns + "DuplexCapability").Any();
            bool supportsColor = root.Descendants(ns + "ColorCapability").Any();
            bool supportsCollation = root.Descendants(ns + "CollationCapability").Any();

            // Parse paper sizes from XPS PrintCapabilities schema
            // Structure: <PageMediaSize><PageMediaSize.Option><PageMediaSize PageSize Width="..." Height="..." DisplayName="..."/></PageMediaSize.Option></PageMediaSize>
            var paperSizes = new List<PaperSizeInfo>();

            // Try 1: XPS standard — PageSize child element with Width/Height attributes
            foreach (var psElem in root.Descendants(ns + "PageMediaSize"))
            {
                var displayName = psElem.Attribute("DisplayName")?.Value;

                // Check for PageSize child element
                var pageSizeElem = psElem.Element(ns + "PageSize");
                if (pageSizeElem is not null)
                {
                    var widthAttr = pageSizeElem.Attribute("Width")?.Value;
                    var heightAttr = pageSizeElem.Attribute("Height")?.Value;

                    if (displayName is not null && widthAttr is not null && heightAttr is not null
                        && double.TryParse(widthAttr, out double w) && double.TryParse(heightAttr, out double h))
                    {
                        // XPS uses 1/96 inch units
                        paperSizes.Add(new PaperSizeInfo
                        {
                            Name = displayName,
                            WidthMm = (float)(w / 96.0 * 25.4),
                            HeightMm = (float)(h / 96.0 * 25.4),
                            PaperKindId = 0
                        });
                        continue;
                    }
                }

                // Try 2: Attributes directly on PageMediaSize (some printers)
                var widthAttrDirect = psElem.Attribute("Width")?.Value;
                var heightAttrDirect = psElem.Attribute("Height")?.Value;

                if (displayName is not null && widthAttrDirect is not null && heightAttrDirect is not null
                    && double.TryParse(widthAttrDirect, out double wd) && double.TryParse(heightAttrDirect, out double hd))
                {
                    // Could be 1/96 inch or 0.1mm — try to detect by magnitude
                    // A4 is ~210mm x ~297mm. If values are > 1000, likely 1/96 inch. If < 100, likely 0.1mm.
                    bool isNinetySixthInch = wd > 1000 || hd > 1000;
                    paperSizes.Add(new PaperSizeInfo
                    {
                        Name = displayName,
                        WidthMm = isNinetySixthInch ? (float)(wd / 96.0 * 25.4) : (float)(wd / 10.0),
                        HeightMm = isNinetySixthInch ? (float)(hd / 96.0 * 25.4) : (float)(hd / 10.0),
                        PaperKindId = 0
                    });
                }
            }

            // Try 3: ImageableArea — some printers report sizes via MediaSizeWidth/MediaSizeHeight
            if (paperSizes.Count == 0)
            {
                foreach (var option in root.Descendants(ns + "PageMediaSize").Descendants(ns + "Option"))
                {
                    var name = option.Attribute("DisplayName")?.Value;
                    var width = option.Element(ns + "MediaSizeWidth")?.Attribute("Value")?.Value;
                    var height = option.Element(ns + "MediaSizeHeight")?.Attribute("Value")?.Value;

                    if (name is not null && width is not null && height is not null
                        && double.TryParse(width, out double w) && double.TryParse(height, out double h))
                    {
                        paperSizes.Add(new PaperSizeInfo
                        {
                            Name = name,
                            WidthMm = (float)(w / 96.0 * 25.4),
                            HeightMm = (float)(h / 96.0 * 25.4),
                            PaperKindId = 0
                        });
                    }
                }
            }

            var snapshot = new PrinterCapabilitySnapshot
            {
                PrinterName = printerName,
                DriverName = queue.Description ?? "System.Printing",
                SupportsDuplex = supportsDuplex,
                SupportsColor = supportsColor,
                SupportsCollation = supportsCollation,
                PaperSizes = paperSizes,
                DiscoverySource = "System.Printing"
            };

            logger.LogInformation("ModernCapabilityProvider: Found {PaperCount} paper sizes, Duplex={HasDuplex}, Color={HasColor} for '{PrinterName}'",
                paperSizes.Count, supportsDuplex, supportsColor, printerName);

            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ModernCapabilityProvider: Failed to query '{PrinterName}'", printerName);
            return null;
        }
    }
}
