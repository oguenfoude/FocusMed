namespace FocusMed.Dicom.Options;

public class StorageForwardTarget
{
    public string Name { get; set; } = string.Empty;
    public string AeTitle { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
    public string ScuAe { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
