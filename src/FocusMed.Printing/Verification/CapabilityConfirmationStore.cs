using System.Text.Json;
using FocusMed.Printing.Discovery;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Verification;

internal sealed class CapabilityConfirmationStore(
    ILogger<CapabilityConfirmationStore> logger) : ICapabilityConfirmationStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string GetFilePath()
    {
        var dataDir = DataDirectoryHelper.GetDataDirectory();
        return Path.Combine(dataDir, "capability-confirmations.json");
    }

    public async Task<Dictionary<string, bool>> GetAllAsync(string printerName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath))
                return [];

            var json = await File.ReadAllTextAsync(filePath, ct);
            var allData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(json, JsonDefaults.Indented) ?? [];
            return allData.TryGetValue(printerName, out var caps) ? caps : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read capability confirmations for '{PrinterName}'", printerName);
            return [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string printerName, string capabilityKey, bool confirmedWorking, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Dictionary<string, Dictionary<string, bool>> allData;
            if (File.Exists(filePath))
            {
                var json = await File.ReadAllTextAsync(filePath, ct);
                allData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(json, JsonDefaults.Indented) ?? [];
            }
            else
            {
                allData = [];
            }

            if (!allData.TryGetValue(printerName, out var caps))
            {
                caps = [];
                allData[printerName] = caps;
            }

            caps[capabilityKey] = confirmedWorking;

            var updatedJson = JsonSerializer.Serialize(allData, JsonDefaults.Indented);
            await File.WriteAllTextAsync(filePath, updatedJson, ct);

            logger.LogInformation("Saved capability confirmation: '{PrinterName}' -> {Key} = {Value}", printerName, capabilityKey, confirmedWorking);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save capability confirmation for '{PrinterName}' -> {Key}", printerName, capabilityKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string printerName, string capabilityKey, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath)) return;

            var json = await File.ReadAllTextAsync(filePath, ct);
            var allData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(json, JsonDefaults.Indented) ?? [];

            if (allData.TryGetValue(printerName, out var caps))
            {
                caps.Remove(capabilityKey);
                if (caps.Count == 0)
                    allData.Remove(printerName);
            }

            var updatedJson = JsonSerializer.Serialize(allData, JsonDefaults.Indented);
            await File.WriteAllTextAsync(filePath, updatedJson, ct);

            logger.LogInformation("Removed capability confirmation: '{PrinterName}' -> {Key}", printerName, capabilityKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove capability confirmation for '{PrinterName}' -> {Key}", printerName, capabilityKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAllAsync(string printerName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath)) return;

            var json = await File.ReadAllTextAsync(filePath, ct);
            var allData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(json, JsonDefaults.Indented) ?? [];

            allData.Remove(printerName);

            var updatedJson = JsonSerializer.Serialize(allData, JsonDefaults.Indented);
            await File.WriteAllTextAsync(filePath, updatedJson, ct);

            logger.LogInformation("Removed all capability confirmations for '{PrinterName}'", printerName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove all capability confirmations for '{PrinterName}'", printerName);
        }
        finally
        {
            _lock.Release();
        }
    }
}
