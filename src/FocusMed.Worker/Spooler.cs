using System.Drawing.Printing;
using FocusMed.Dicom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FocusMed.Worker;

public class Spooler : ISpooler
{
    private readonly ILogger<Spooler> _logger;
    private readonly string _targetPrinterName;

    public Spooler(ILogger<Spooler> logger, IConfiguration configuration)
    {
        _logger = logger;
        _targetPrinterName = configuration.GetValue<string>("TargetPrinterName") ?? string.Empty;
    }

    public void PrintImage(string imagePath)
    {
        _logger.LogInformation("Routing image {ImagePath} to printer...", imagePath);

        using var pd = new PrintDocument();
        
        if (!string.IsNullOrWhiteSpace(_targetPrinterName))
        {
            pd.PrinterSettings.PrinterName = _targetPrinterName;
        }

        pd.PrintPage += (sender, e) =>
        {
            using var img = System.Drawing.Image.FromFile(imagePath);
            e.Graphics?.DrawImage(img, e.PageBounds);
        };

        try
        {
            pd.Print();
            _logger.LogInformation("Successfully spooled print job.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to print document.");
        }
    }
}
