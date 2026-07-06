namespace FocusMed.Dicom.Options;

public class FilmPrinterConfig
{
    public string Name { get; set; } = string.Empty;
    public string ScuAe { get; set; } = string.Empty;
    public string PrinterIp { get; set; } = string.Empty;
    public int PrinterPort { get; set; }
    public string PrinterAe { get; set; } = string.Empty;
    public string FilmTarget { get; set; } = "PROCESSOR";
    public string FilmType { get; set; } = "PAPER";
    public string Priority { get; set; } = "HIGH";
    public PrinterType PrinterType { get; set; } = PrinterType.GrayLevel;
    public bool Enabled { get; set; } = true;
}
