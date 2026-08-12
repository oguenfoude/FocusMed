using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Profiles;

internal sealed class PrinterSettingsStore(
    ILogger<PrinterSettingsStore> logger) : IPrinterSettingsStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string GetFilePath()
    {
        var dataDir = DataDirectoryHelper.GetDataDirectory();
        return Path.Combine(dataDir, "printer-settings.json");
    }

    public async Task<PrintSettings> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath))
                return new PrintSettings();

            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<PrintSettings>(json, JsonDefaults.Indented) ?? new PrintSettings();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load printer settings");
            return new PrintSettings();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(PrintSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, JsonDefaults.Indented);
            await File.WriteAllTextAsync(filePath, json, ct);

            logger.LogInformation("Saved printer settings: DefaultPrinter='{Printer}', DefaultProfile='{Profile}', Copies={Copies}",
                settings.DefaultPrinterName, settings.DefaultProfileName, settings.DefaultCopies);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save printer settings");
        }
        finally
        {
            _lock.Release();
        }
    }
}
