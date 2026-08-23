namespace FocusMed.Printing.Jobs;

public class RawPrinterConfig
{
    public const string SectionName = "RawPrinters";

    public List<RawPrinterPreset> Printers { get; set; } = [];
}

public class RawPrinterPreset
{
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public string PaperSize { get; set; } = "A3";
    public bool IsBooklet { get; set; } = true;
    public bool ForceGrayscale { get; set; }
    public int Copies { get; set; } = 1;
    public string? WindowsPrinterName { get; set; }
}
