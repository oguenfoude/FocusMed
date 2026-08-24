namespace FocusMed.Dicom;

public interface IStudyNotificationService
{
    event Action? OnStudyChanged;
    void NotifyStudyChanged();
}

/// <summary>
/// Throttles per-file C-STORE notifications into at most two UI refreshes per window:
/// one leading (instant feedback) and one trailing (final state).
/// Without this, a 500-image study triggers 500 full list reloads on every open dashboard.
/// </summary>
public sealed class StudyNotificationService : IStudyNotificationService
{
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMilliseconds(1500);

    private readonly object _gate = new();
    private Timer? _trailingTimer;
    private DateTime _lastFireUtc = DateTime.MinValue;

    public event Action? OnStudyChanged;

    public void NotifyStudyChanged()
    {
        Action? leading = null;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastFireUtc >= ThrottleWindow)
            {
                _lastFireUtc = now;
                DisposeTrailingTimerLocked();
                leading = OnStudyChanged;
            }
            else
            {
                _trailingTimer ??= new Timer(
                    _ => FireTrailing(), null, ThrottleWindow, Timeout.InfiniteTimeSpan);
            }
        }

        leading?.Invoke();
    }

    private void FireTrailing()
    {
        lock (_gate)
        {
            _lastFireUtc = DateTime.UtcNow;
            DisposeTrailingTimerLocked();
        }

        OnStudyChanged?.Invoke();
    }

    private void DisposeTrailingTimerLocked()
    {
        _trailingTimer?.Dispose();
        _trailingTimer = null;
    }
}
