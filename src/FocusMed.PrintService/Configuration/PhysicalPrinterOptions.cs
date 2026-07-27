namespace FocusMed.PrintService.Configuration;

public class PhysicalPrinterOptions
{
    public List<PhysicalPrinterConfig> PhysicalPrinters { get; set; } = new();
}

public class PhysicalPrinterConfig
{
    /// <summary>
    /// Display name for the printer (e.g. "Brother MFC-J6720DW").
    /// Used for matching against InstalledPrinters — auto-detects the best driver.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Optional explicit Windows queue name override. If empty, auto-detected from Name.
    /// </summary>
    public string? WindowsQueueName { get; set; }

    public string Protocol { get; set; } = "generic-driver";
    public bool Enabled { get; set; } = true;
    public string? PreferredPaperSize { get; set; }
}
