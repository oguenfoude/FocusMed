namespace FocusMed.Printing.Profiles;

public interface IPrinterSettingsStore
{
    Task<PrintSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(PrintSettings settings, CancellationToken ct = default);
}
