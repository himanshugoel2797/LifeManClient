namespace Lifeman.Client.Collectors;

/// One observation source. The platform head provides one implementation
/// per device surface (phone.battery, desktop.power, phone.foreground_app, …).
/// Volume control (sample-rate, downsampling) lives in the collector, not
/// in the kernel — sensor wakelocks per sample matter.
public interface ICollector
{
    /// Routing key. Must match a kernel-side handler (see CLIENT_DESIGN
    /// "Observation surfaces"). Inventing a new surface needs a server change.
    string Surface { get; }

    IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct);
}

public sealed record CollectedEvent(
    string Surface,
    string PayloadJson,
    DateTimeOffset EmittedAt);
