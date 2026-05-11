namespace Lifeman.Client.Outbox;

public interface IOutbox : IAsyncDisposable
{
    ValueTask InitAsync(CancellationToken ct = default);

    /// Append a new event to the outbox. The payload is opaque JSON — the
    /// kernel's input router decides what it means by routing on `surface`.
    /// Set `isCritical` for events that must survive a size-cap trim
    /// (e.g. surfaced from `urgency=urgent` flows the user explicitly
    /// flagged as load-bearing).
    ValueTask<long> EnqueueAsync(string surface, string payloadJson, DateTimeOffset emittedAt, bool isCritical = false, CancellationToken ct = default);

    /// Peek the oldest N pending entries without removing them. Returned
    /// oldest-first so the kernel sees events in roughly the order they
    /// happened.
    ValueTask<IReadOnlyList<OutboxEntry>> PeekAsync(int max, CancellationToken ct = default);

    /// Mark entries as successfully uploaded (deletes them).
    ValueTask AckAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// Mark entries as failed. Increments attempts; stores last error.
    /// Permanent (non-retryable) failures are deleted to avoid poison-pill loops.
    ValueTask FailAsync(IReadOnlyCollection<long> ids, string error, bool permanent, CancellationToken ct = default);

    /// Total queued entries, including failed ones still under retry.
    ValueTask<long> CountAsync(CancellationToken ct = default);

    /// Drop oldest entries until the on-disk size is below the cap. Returns
    /// number dropped. Rows flagged `is_critical = 1` are preserved.
    ValueTask<int> TrimAsync(long maxBytes, CancellationToken ct = default);

    /// Atomically record that we've surfaced an output, so SSE-replay
    /// after reconnect doesn't re-fire the same notification. Returns
    /// `true` if this was a new ID (caller should render), `false` if
    /// it was already seen (caller should skip). Entries older than the
    /// in-memory replay window are pruned by `TrimReceivedAsync`.
    ValueTask<bool> TryMarkReceivedAsync(string outputId, DateTimeOffset receivedAt, CancellationToken ct = default);

    /// Drop received-output records older than `retain`. Bounded so the
    /// dedup table doesn't grow forever; 30 days easily covers any
    /// realistic SSE-replay or pending-fetch overlap.
    ValueTask<int> TrimReceivedAsync(TimeSpan retain, CancellationToken ct = default);
}
