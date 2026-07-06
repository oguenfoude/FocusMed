namespace FocusMed.Dicom;

public record StorageForwardRequest(string FilePath, string SopInstanceUid, string SopClassUid);

public interface IStorageForwardQueue
{
    void Enqueue(StorageForwardRequest request);
    IAsyncEnumerable<StorageForwardRequest> ReadAllAsync(CancellationToken ct);
    void Complete();
    int PendingCount { get; }
}

public sealed class StorageForwardQueue : IStorageForwardQueue
{
    private readonly System.Threading.Channels.Channel<StorageForwardRequest> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<StorageForwardRequest>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public void Enqueue(StorageForwardRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<StorageForwardRequest> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.TryComplete();

    public int PendingCount => _channel.Reader.Count;
}
