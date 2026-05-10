using System.Text.Json;
using Lifeman.Client.Collectors;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;
using Lifeman.Client.Renderers;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Hosting;

/// Wires the shared core pieces together. The platform head constructs
/// this with concrete IConfigStore (keystore-backed), IRenderer, and one
/// or more ICollectors, then awaits RunAsync.
public sealed class LifemanClientHost : IAsyncDisposable
{
    private readonly IOutbox _outbox;
    private readonly Uploader _uploader;
    private readonly SseReceiver _sse;
    private readonly IRenderer _renderer;
    private readonly IReadOnlyList<ICollector> _collectors;
    private readonly ILogger<LifemanClientHost> _logger;

    public LifemanClientHost(
        IOutbox outbox,
        Uploader uploader,
        SseReceiver sse,
        IRenderer renderer,
        IReadOnlyList<ICollector> collectors,
        ILogger<LifemanClientHost>? logger = null)
    {
        _outbox = outbox;
        _uploader = uploader;
        _sse = sse;
        _renderer = renderer;
        _collectors = collectors;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LifemanClientHost>.Instance;
        _sse.OnDeliver += (deliver, ct) => _renderer.ShowAsync(deliver, ct);
        _sse.OnCancel += (cancel, ct) => _renderer.DismissAsync(cancel.OutputId, ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _outbox.InitAsync(ct).ConfigureAwait(false);

        var tasks = new List<Task>
        {
            _uploader.RunAsync(ct),
            _sse.RunAsync(ct),
        };
        foreach (var collector in _collectors)
            tasks.Add(RunCollectorAsync(collector, ct));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RunCollectorAsync(ICollector collector, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in collector.StreamAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                await _outbox.EnqueueAsync(ev.Surface, ev.PayloadJson, ev.EmittedAt, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "collector {Surface} crashed", collector.Surface);
            // Surface the failure as a self-audit event so the kernel can
            // flag a broken collector in the audit log.
            var payload = JsonSerializer.Serialize(new
            {
                collector = collector.Surface,
                error = ex.GetType().FullName,
                message = ex.Message,
            });
            try
            {
                await _outbox.EnqueueAsync("client.collector_failure", payload, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            }
            catch
            {
                // outbox is unhappy; swallow — the next collector heartbeat will surface it.
            }
        }
    }

    public ValueTask DisposeAsync() => _outbox.DisposeAsync();
}
