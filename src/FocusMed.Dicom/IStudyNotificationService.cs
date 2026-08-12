namespace FocusMed.Dicom;

public interface IStudyNotificationService
{
    event Action? OnStudyChanged;
    void NotifyStudyChanged();
}

public class StudyNotificationService : IStudyNotificationService
{
    public event Action? OnStudyChanged;

    public void NotifyStudyChanged()
    {
        OnStudyChanged?.Invoke();
    }
}
