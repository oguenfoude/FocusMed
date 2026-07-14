namespace FocusMed.Dicom.Options;

public class PngExtractionOptions
{
    public const string SectionName = "PngExtraction";

    public bool Enabled { get; set; } = true;
    public int CleanupIntervalMinutes { get; set; } = 15;
    public int MaxAgeMinutes { get; set; } = 60;
}
