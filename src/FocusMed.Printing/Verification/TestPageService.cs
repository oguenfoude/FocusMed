using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Printing;
using FocusMed.Printing.Discovery;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Verification;

internal sealed class TestPageService(
    ICapabilityConfirmationStore confirmationStore,
    ILogger<TestPageService> logger) : ITestPageService
{
    private static readonly ConcurrentDictionary<string, PendingTestJob> PendingJobs = new();
    private static readonly Timer CleanupTimer = new(CleanupStaleJobs, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    public async Task<string> PrintTestPageAsync(string printerName, string settingToTest, CancellationToken ct = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];

        logger.LogInformation("Printing test page for '{PrinterName}' (setting: {Setting}, jobId: {JobId})", printerName, settingToTest, jobId);

        using var doc = new PrintDocument
        {
            PrinterSettings = new PrinterSettings
            {
                PrinterName = printerName,
                Copies = 1
            }
        };

        doc.PrintPage += (sender, e) =>
        {
            var g = e.Graphics!;
            var pageRect = e.MarginBounds;

            g.FillRectangle(Brushes.White, pageRect);

            using var titleFont = new Font("Arial", 24, FontStyle.Bold);
            using var bodyFont = new Font("Arial", 14);
            using var settingFont = new Font("Arial", 18, FontStyle.Bold);

            g.DrawString("FocusMed - Test Page", titleFont, Brushes.Black, pageRect.Left, pageRect.Top);

            var settingY = pageRect.Top + 60;
            g.DrawString($"Testing: {settingToTest}", settingFont, Brushes.DarkBlue, pageRect.Left, settingY);

            var bodyY = settingY + 40;
            g.DrawString($"Printer: {printerName}", bodyFont, Brushes.Black, pageRect.Left, bodyY);
            g.DrawString($"Job ID: {jobId}", bodyFont, Brushes.Black, pageRect.Left, bodyY + 25);
            g.DrawString($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", bodyFont, Brushes.Black, pageRect.Left, bodyY + 50);

            var instructY = bodyY + 100;
            g.DrawString("Did this test page print correctly?", bodyFont, Brushes.Black, pageRect.Left, instructY);
            g.DrawString($"Expected setting: {settingToTest}", bodyFont, Brushes.DarkRed, pageRect.Left, instructY + 25);

            e.HasMorePages = false;
        };

        await Task.Run(() => doc.Print(), ct);

        PendingJobs[jobId] = new PendingTestJob(printerName, settingToTest, DateTime.UtcNow);

        logger.LogInformation("Test page printed successfully for '{PrinterName}' (jobId: {JobId})", printerName, jobId);

        return jobId;
    }

    public async Task ConfirmTestResultAsync(string testJobId, bool wasSuccessful, CancellationToken ct = default)
    {
        if (!PendingJobs.TryRemove(testJobId, out var job))
        {
            logger.LogWarning("Test job '{JobId}' not found or already confirmed", testJobId);
            return;
        }

        await confirmationStore.SaveAsync(job.PrinterName, job.SettingToTest, wasSuccessful, ct);

        logger.LogInformation("Test result confirmed for '{PrinterName}' -> {Setting}: {Result}",
            job.PrinterName, job.SettingToTest, wasSuccessful ? "Confirmed Working" : "Confirmed Not Working");
    }

    private static void CleanupStaleJobs(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var kvp in PendingJobs)
        {
            if (kvp.Value.CreatedAt < cutoff)
                PendingJobs.TryRemove(kvp.Key, out _);
        }
    }

    private sealed record PendingTestJob(string PrinterName, string SettingToTest, DateTime CreatedAt);
}
