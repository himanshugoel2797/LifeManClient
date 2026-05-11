using System.Text.Json;

namespace Lifeman.Client.Collectors;

/// `client.heartbeat` — emits one event on startup, then one every
/// interval. Useful as a liveness signal: on a fully-quiet network /
/// idle device, the heads otherwise produce no traffic, which makes
/// "agent is alive" indistinguishable from "agent crashed silently".
public sealed class HeartbeatCollector : ICollector
{
    private readonly TimeSpan _interval;
    public string Surface => "client.heartbeat";

    public HeartbeatCollector(TimeSpan? interval = null)
        => _interval = interval ?? TimeSpan.FromMinutes(5);

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
