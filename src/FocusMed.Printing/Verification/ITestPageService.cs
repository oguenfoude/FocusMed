namespace FocusMed.Printing.Verification;

public interface ITestPageService
{
    Task<string> PrintTestPageAsync(string printerName, string settingToTest, CancellationToken ct = default);
    Task ConfirmTestResultAsync(string testJobId, bool wasSuccessful, CancellationToken ct = default);
}
