using System.Threading.Channels;

namespace FocusMed.Data.Messaging;

public record StudyCompletedEvent(string PatientId, string PatientName, string Modality, DateTime StudyDate);

public interface IStudyEventBus
{
    void Publish(StudyCompletedEvent e);
    IAsyncEnumerable<StudyCompletedEvent> Subscribe(CancellationToken ct);
}

public class InMemoryStudyEventBus : IStudyEventBus
{
    private readonly Channel<StudyCompletedEvent> _channel = Channel.CreateUnbounded<StudyCompletedEvent>();

    public void Publish(StudyCompletedEvent e)
    {
        _channel.Writer.TryWrite(e);
    }

    public IAsyncEnumerable<StudyCompletedEvent> Subscribe(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
