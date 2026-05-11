using System.Text.Json;

namespace Lifeman.Client.Outbox;

/// Helper for the heads to drop one-off `client.*` events into the
/// outbox without going through the ICollector / host-loop machinery.
/// Used for renderer side-channel signals like notification dismissal
/// that originate in BroadcastReceiver / event-handler callbacks that
/// don't sit inside the collector pipeline.
public static class ClientEvents
{
    public const string OutputEventSurface = "client.output_event";

    public static async Task EnqueueOutputEventAsync(
        IOutbox? outbox,
        string trigger,
        string outputId,
        string? action = null,
        CancellationToken ct = default)
    {
        if (outbox is null) return;
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            output_id = outputId,
            action,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        try { await outbox.EnqueueAsync(OutputEventSurface, payload, DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best-effort side channel */ }
    }
}
