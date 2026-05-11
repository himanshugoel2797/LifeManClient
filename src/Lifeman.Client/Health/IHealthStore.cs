namespace Lifeman.Client.Health;

/// Per-collector liveness ledger. Lets the self-audit emitter spot a
/// collector that's been silent for too long even when no exception was
/// thrown — Android OEMs love to revoke permissions or kill listener
/// services without telling anyone.
public interface IHealthStore
{
    ValueTask RecordSuccessAsync(string surface, CancellationToken ct = default);
    ValueTask RecordErrorAsync(string surface, string error, CancellationToken ct = default);
    ValueTask<IReadOnlyList<HealthEntry>> SnapshotAsync(CancellationToken ct = default);
}

public sealed record HealthEntry(
    string Surface,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError,
    long SuccessCount,
    long ErrorCount);

public sealed class NullHealthStore : IHealthStore
{
    public ValueTask RecordSuccessAsync(string surface, CancellationToken ct = default) => default;
    public ValueTask RecordErrorAsync(string surface, string error, CancellationToken ct = default) => default;
    public ValueTask<IReadOnlyList<HealthEntry>> SnapshotAsync(CancellationToken ct = default)
        => new(Array.Empty<HealthEntry>());
}
