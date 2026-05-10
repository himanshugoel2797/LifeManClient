using System.Text.Json;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.DevHost.Collectors;

/// Cross-platform `client.heartbeat` — emits one event on startup, then one
/// every interval. Useful for smoke-testing the upload loop without any
/// platform-specific permission surface.
public sealed class HeartbeatCollector : ICollector
{
    private readonly TimeSpan _interval;

    public HeartbeatCollector(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public string Surface => "client.heartbeat";

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var seq = 0L;
        while (!ct.IsCancellationRequested)
        {
            var payload = JsonSerializer.Serialize(new
            {
                seq = seq++,
                machine = Environment.MachineName,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            yield return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }
}
