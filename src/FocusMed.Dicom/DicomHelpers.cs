namespace FocusMed.Dicom;

internal static class DicomHelpers
{
    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace('\\', '_').Replace('/', '_');
        return string.IsNullOrWhiteSpace(name) ? "" : name.Trim().Replace(' ', '_');
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
}
