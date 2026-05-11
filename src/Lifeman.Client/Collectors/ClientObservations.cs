using System.Text.Json;

namespace Lifeman.Client.Collectors;

/// Self-audit signals collectors emit so the kernel can tell the
/// difference between "this sensor is quiet" and "this sensor is dead".
/// Surfaced as `client.observation`; the kernel routes those into the
/// audit log without treating them as primary surface data.
public static class ClientObservations
{
    public const string Surface = "client.observation";

    /// Emit on first run when a permission-gated collector self-disables.
    /// Without this, the kernel sees "no foreground_app events" identically
    /// for "user hasn't switched apps" and "user revoked PACKAGE_USAGE_STATS"
    /// — which masks a broken pipeline.
    public static CollectedEvent CollectorDisabled(string surface, string reason)
    {
        var payload = JsonSerializer.Serialize(new
        {
            kind = "collector_disabled",
            surface,
            reason,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }
}
