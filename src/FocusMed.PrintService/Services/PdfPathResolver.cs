using System.IO;

namespace FocusMed.PrintService.Services;

internal static class PdfPathResolver
{
    public static string? Resolve(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            return null;

        if (Path.IsPathRooted(pdfPath))
            return File.Exists(pdfPath) ? pdfPath : null;

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FocusMed");

        var full = Path.Combine(dataDir, pdfPath);
        return File.Exists(full) ? full : null;
    }
}
