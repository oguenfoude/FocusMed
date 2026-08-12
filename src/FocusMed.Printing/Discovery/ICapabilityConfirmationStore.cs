namespace FocusMed.Printing.Discovery;

public interface ICapabilityConfirmationStore
{
    Task<Dictionary<string, bool>> GetAllAsync(string printerName, CancellationToken ct = default);
    Task SaveAsync(string printerName, string capabilityKey, bool confirmedWorking, CancellationToken ct = default);
    Task RemoveAsync(string printerName, string capabilityKey, CancellationToken ct = default);
    Task RemoveAllAsync(string printerName, CancellationToken ct = default);
}
