using System.Collections.Concurrent;
using FocusMed.PrintService.Abstractions;

namespace FocusMed.PrintService.Services;

public sealed class JobStateTracker
{
    private readonly ConcurrentDictionary<string, JobStatus> _states = new();
    private int _next = 0;

    public int NextId() => Interlocked.Increment(ref _next);

    public void Register(string printerName, int jobId)
    {
        var key = Key(printerName, jobId);
        _states.TryAdd(key, new JobStatus("Queued", null));
    }

    public void MarkPrinting(string printerName, int jobId)
    {
        var key = Key(printerName, jobId);
        _states[key] = new JobStatus("Printing", null);
    }

    public void MarkCompleted(string printerName, int jobId)
    {
        var key = Key(printerName, jobId);
        _states[key] = new JobStatus("Completed", null);
    }

    public void MarkError(string printerName, int jobId, string message)
    {
        var key = Key(printerName, jobId);
        _states[key] = new JobStatus("Error", message);
    }

    public JobStatus Get(string printerName, int jobId)
    {
        var key = Key(printerName, jobId);
        return _states.TryGetValue(key, out var status)
            ? status
            : new JobStatus("NotFound", $"Aucune tache #{jobId} pour l'imprimante '{printerName}'.");
    }

    private static string Key(string printerName, int jobId) => $"{printerName}::{jobId}";
}
