using System.Drawing.Printing;
using FocusMed.PrintService.Configuration;

namespace FocusMed.PrintService.Services;

internal static class PaperSizePolicy
{
    public static PaperSize? Resolve(PhysicalPrinterConfig config, PrinterSettings probe)
    {
        if (probe.PaperSizes == null || probe.PaperSizes.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(config.PreferredPaperSize))
        {
            var match = FindPaperSize(probe.PaperSizes, config.PreferredPaperSize);
            if (match != null)
                return match;
        }

        var fallback = FindPaperSize(probe.PaperSizes, "A4");
        if (fallback != null)
            return fallback;

        return probe.PaperSizes
            .Cast<PaperSize>()
            .OrderBy(p => Math.Abs(p.Width - 210) + Math.Abs(p.Height - 297))
            .FirstOrDefault();
    }

    public static IReadOnlyList<string> AvailablePaperSizes(PrinterSettings probe)
    {
        if (probe.PaperSizes == null || probe.PaperSizes.Count == 0)
            return Array.Empty<string>();
        return probe.PaperSizes
            .Cast<PaperSize>()
            .Select(p => p.PaperName)
            .ToArray();
    }

    private static PaperSize? FindPaperSize(System.Drawing.Printing.PrinterSettings.PaperSizeCollection sizes, string requested)
    {
        var key = requested.Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant();
        foreach (PaperSize size in sizes)
        {
            var name = size.PaperName.Replace(" ", "").Replace("-", "").ToLowerInvariant();
            if (name == key)
                return size;
            if (name.Contains(key))
                return size;
        }
        return null;
    }
}
