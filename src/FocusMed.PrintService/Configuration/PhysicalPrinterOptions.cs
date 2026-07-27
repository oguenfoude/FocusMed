namespace FocusMed.PrintService.Configuration;

public class PhysicalPrinterOptions
{
    public List<PhysicalPrinterConfig> PhysicalPrinters { get; set; } = new();
}

public class PhysicalPrinterConfig
{
    public string Name { get; set; } = "";
    public string WindowsQueueName { get; set; } = "";
    public string Protocol { get; set; } = "generic-driver";
    public bool Enabled { get; set; } = true;
    public string? PreferredPaperSize { get; set; }
}
