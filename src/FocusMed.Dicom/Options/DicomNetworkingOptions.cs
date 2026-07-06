namespace FocusMed.Dicom.Options;

public class DicomNetworkingOptions
{
    public const string SectionName = "DicomNetworking";

    public string AETitle { get; set; } = "FOCUSMED_SCP";
    public int MaxPduSize { get; set; } = 65536;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int DicomPort { get; set; } = 11112;
    public bool EnforceAeWhitelist { get; set; }
    public List<string> SupportedTransferSyntaxes { get; set; } = [];
    public List<AllowedCallingAe> AllowedCallingAETitles { get; set; } = [];
    public Dictionary<string, AeEndpoint> StorageCommitmentScuMapping { get; set; } = [];
    public List<FilmPrinterConfig> FilmPrinters { get; set; } = [];
    public List<StorageForwardTarget> StorageForwardTargets { get; set; } = [];
}

public class AllowedCallingAe
{
    public string AETitle { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
}

public class AeEndpoint
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; }
}
