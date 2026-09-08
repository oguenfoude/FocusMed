namespace FocusMed.Dicom;

public static class DicomHelpers
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        var hasInvalid = false;
        foreach (var c in name)
        {
            if (c == '\\' || c == '/' || c == ' ' || Array.IndexOf(InvalidFileNameChars, c) >= 0)
            {
                hasInvalid = true;
                break;
            }
        }
        if (!hasInvalid) return name;

        return string.Create(name.Length, name, (span, state) =>
        {
            for (int i = 0; i < state.Length; i++)
            {
                var c = state[i];
                span[i] = (c == '\\' || c == '/' || c == ' ' || Array.IndexOf(InvalidFileNameChars, c) >= 0) ? '_' : c;
            }
        });
    }

    public static string GetFnv1aHash(string input)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= 1099511628211;
        }
        return hash.ToString("X16");
    }

    public static DateTime? GetDicomDate(FellowOakDicom.DicomDataset dataset, FellowOakDicom.DicomTag tag)
    {
        var dateString = dataset.GetSingleValueOrDefault(tag, string.Empty);
        if (string.IsNullOrWhiteSpace(dateString))
            return null;
        dateString = dateString.Trim();
        if (DateTime.TryParseExact(dateString, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        return null;
    }

    public static string FormatPatientName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Inconnu";
        return name.Replace("^", " ");
    }

    // Deletion guard for cleanup paths: true only when `dir` is strictly
    // INSIDE `root` (never root itself or anything above it). All archive/png
    // deletions must pass through this — a wrong GetParent chain once pointed
    // at the whole archive/ folder (DeletedCleanupService, 2026-09-08).
    public static bool IsSubdirectoryOf(string dir, string root)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root)) return false;
        var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        return fullDir.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
