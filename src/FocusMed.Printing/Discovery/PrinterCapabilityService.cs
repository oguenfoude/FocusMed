using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Discovery;

internal sealed class PrinterCapabilityService(
    ModernCapabilityProvider modern,
    LegacyCapabilityProvider legacy,
    Win32CapabilityProvider win32,
    ICapabilityConfirmationStore confirmationStore,
    ILogger<PrinterCapabilityService> logger) : IPrinterCapabilityService
{
    public async Task<PrinterCapabilitySnapshot> GetSnapshotAsync(string printerName, CancellationToken ct = default)
    {
        PrinterCapabilitySnapshot? snapshot = null;

        snapshot = modern.TryGet(printerName);
        if (snapshot is not null)
        {
            logger.LogDebug("Using System.Printing provider for '{PrinterName}'", printerName);
        }

        snapshot ??= legacy.TryGet(printerName);
        if (snapshot is not null)
        {
            logger.LogDebug("Using System.Drawing.Printing provider for '{PrinterName}'", printerName);
        }

        snapshot ??= win32.TryGet(printerName);
        if (snapshot is not null)
        {
            logger.LogDebug("Using Win32 DeviceCapabilities provider for '{PrinterName}'", printerName);
        }

        snapshot ??= new PrinterCapabilitySnapshot
        {
            PrinterName = printerName,
            DriverName = "Unknown",
            DiscoverySource = "None"
        };

        // If Modern provider returned 0 paper sizes, enrich from Legacy (GDI+ has PaperSizes)
        // Also copy duplex/color flags — Modern (System.Printing) often reports false for v4 drivers
        if (snapshot.PaperSizes.Count == 0 && snapshot.DiscoverySource == "System.Printing")
        {
            var legacySnap = legacy.TryGet(printerName);
            if (legacySnap is not null && legacySnap.PaperSizes.Count > 0)
            {
                snapshot = snapshot with
                {
                    PaperSizes = legacySnap.PaperSizes,
                    PaperTrays = legacySnap.PaperTrays,
                    Resolutions = legacySnap.Resolutions,
                    SupportsDuplex = legacySnap.SupportsDuplex,
                    SupportsColor = legacySnap.SupportsColor,
                    SupportsCollation = legacySnap.SupportsCollation,
                    PaperToTrayMap = legacySnap.PaperToTrayMap
                };
                logger.LogInformation("Enriched '{PrinterName}' from Legacy: {PaperCount} papers, Duplex={Duplex}, Color={Color}",
                    printerName, legacySnap.PaperSizes.Count, legacySnap.SupportsDuplex, legacySnap.SupportsColor);
            }
        }

        // Enrich PaperToTrayMap from Win32 DEVMODE probing if current map is empty
        // Win32 uses DocumentProperties to probe each tray's default paper — most reliable method
        if (snapshot.PaperToTrayMap.Count == 0)
        {
            var win32Snap = win32.TryGet(printerName);
            if (win32Snap is not null && win32Snap.PaperToTrayMap.Count > 0)
            {
                snapshot = snapshot with { PaperToTrayMap = win32Snap.PaperToTrayMap };
                logger.LogInformation("Enriched '{PrinterName}' PaperToTrayMap from Win32: {MapCount} entries ({Entries})",
                    printerName, win32Snap.PaperToTrayMap.Count,
                    string.Join(", ", win32Snap.PaperToTrayMap.Select(kv => $"{kv.Key}->bin{kv.Value}")));
            }
        }

        // Apply test-page confirmations for any known capability key
        var confirmations = await confirmationStore.GetAllAsync(printerName, ct);
        if (confirmations.TryGetValue(ConfirmationKeys.Duplex, out bool duplexConfirmed))
        {
            snapshot = snapshot with { SupportsDuplex = duplexConfirmed };
            logger.LogInformation("Applied test-page confirmation for Duplex on '{PrinterName}': {Confirmed}", printerName, duplexConfirmed);
        }
        if (confirmations.TryGetValue(ConfirmationKeys.Color, out bool colorConfirmed))
        {
            snapshot = snapshot with { SupportsColor = colorConfirmed };
        }
        if (confirmations.TryGetValue(ConfirmationKeys.Collation, out bool collationConfirmed))
        {
            snapshot = snapshot with { SupportsCollation = collationConfirmed };
        }

        return snapshot;
    }
}
