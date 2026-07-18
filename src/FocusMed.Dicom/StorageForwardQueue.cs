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

    private int _pendingCount;

    public void Enqueue(StorageForwardRequest request)
    {
        if (_channel.Writer.TryWrite(request))
            Interlocked.Increment(ref _pendingCount);
    }

    public async IAsyncEnumerable<StorageForwardRequest> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _pendingCount);
            yield return request;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    public int PendingCount => Volatile.Read(ref _pendingCount);
}
