namespace Lifeman.Client.Hosting;

/// Live counters the host components stamp as work happens. Heads read
/// this to build a `status` view; the type is in shared core because
/// every head needs the same shape and the uploader / SSE receiver only
/// know about shared interfaces.
public sealed class ClientRuntimeMetrics
{
    private long _uploadedTicks;
    private long _lastSseEventTicks;
    private long _lastSseConnectTicks;
    private long _eventsUploaded;
    private long _eventsRendered;

    public DateTimeOffset? LastSuccessfulUploadAt
        => _uploadedTicks == 0 ? null : new DateTimeOffset(_uploadedTicks, TimeSpan.Zero);
    public DateTimeOffset? LastSseEventAt
        => _lastSseEventTicks == 0 ? null : new DateTimeOffset(_lastSseEventTicks, TimeSpan.Zero);
    public DateTimeOffset? LastSseConnectAt
        => _lastSseConnectTicks == 0 ? null : new DateTimeOffset(_lastSseConnectTicks, TimeSpan.Zero);
    public long EventsUploaded => Interlocked.Read(ref _eventsUploaded);
    public long EventsRendered => Interlocked.Read(ref _eventsRendered);

    public void RecordUpload(int count)
    {
        Interlocked.Add(ref _eventsUploaded, count);
        Interlocked.Exchange(ref _uploadedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordSseEvent()
        => Interlocked.Exchange(ref _lastSseEventTicks, DateTimeOffset.UtcNow.UtcTicks);

    public void RecordSseConnect()
        => Interlocked.Exchange(ref _lastSseConnectTicks, DateTimeOffset.UtcNow.UtcTicks);

    public void RecordRender()
        => Interlocked.Increment(ref _eventsRendered);
}
