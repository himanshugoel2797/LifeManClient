using System.Text.Json;
using Lifeman.Client.Collectors;
using Lifeman.Client.Contracts;
using Lifeman.Client.Health;
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
    private readonly IHealthStore _health;
    private readonly Updates.UpdateChecker? _updates;
    private readonly ClientRuntimeMetrics? _metrics;
    private readonly long _outboxMaxBytes;
    private readonly ILogger<LifemanClientHost> _logger;
    private readonly Func<OutputCancel, CancellationToken, Task> _onCancelHandler;

    public LifemanClientHost(
        IOutbox outbox,
        Uploader uploader,
        SseReceiver sse,
        IRenderer renderer,
        IReadOnlyList<ICollector> collectors,
        ILogger<LifemanClientHost>? logger = null,
        IHealthStore? health = null,
        Updates.UpdateChecker? updates = null,
        ClientRuntimeMetrics? metrics = null,
        long outboxMaxBytes = 100L * 1024 * 1024)
    {
        _outbox = outbox;
        _uploader = uploader;
        _sse = sse;
        _renderer = renderer;
        _collectors = collectors;
        _health = health ?? new NullHealthStore();
        _updates = updates;
        _metrics = metrics;
        _outboxMaxBytes = outboxMaxBytes;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LifemanClientHost>.Instance;
        _onCancelHandler = (cancel, ct) => _renderer.DismissAsync(cancel.OutputId, ct);
        _sse.OnDeliver += DispatchDeliverAsync;
        _sse.OnCancel += _onCancelHandler;
    }

    private async Task DispatchDeliverAsync(OutputDeliver deliver, CancellationToken ct)
    {
        // Dedup across the SSE-live and /pending replay paths. Both fan
        // through this handler; on reconnect /pending often re-fetches
        // events the device already saw over the live stream.
        var fresh = await _outbox.TryMarkReceivedAsync(deliver.OutputId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (!fresh)
        {
            _logger.LogDebug("skipping duplicate delivery for output {OutputId}", deliver.OutputId);
            return;
        }
        await _renderer.ShowAsync(deliver, ct).ConfigureAwait(false);
        _metrics?.RecordRender();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("host: init outbox");
        await _outbox.InitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("host: outbox ready; starting uploader+sse+{N} collectors", _collectors.Count);

        var tasks = new List<Task>
        {
            _uploader.RunAsync(ct),
            _sse.RunAsync(ct),
            TrimReceivedLoopAsync(ct),
            TrimOutboxLoopAsync(ct),
        };
        if (_updates is not null)
            tasks.Add(_updates.RunAsync(ct));
        foreach (var collector in _collectors)
            tasks.Add(RunCollectorAsync(collector, ct));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task TrimOutboxLoopAsync(CancellationToken ct)
    {
        // Enforce the on-disk size cap per CLIENT_DESIGN §"Local storage
        // & offline". TrimAsync is a no-op below the cap, so polling
        // every ~5 minutes is essentially free.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var dropped = await _outbox.TrimAsync(_outboxMaxBytes, ct).ConfigureAwait(false);
                    if (dropped > 0)
                        _logger.LogWarning("outbox trim dropped {Count} oldest non-critical events to stay under {Cap} bytes",
                            dropped, _outboxMaxBytes);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "outbox trim failed"); }
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task TrimReceivedLoopAsync(CancellationToken ct)
    {
        // Bound the dedup table to ~30 days. The SSE replay buffer is
        // ~256 events and /pending only goes back as far as the durable
        // delivery table; 30 days is comfortably past either window.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _outbox.TrimReceivedAsync(TimeSpan.FromDays(30), ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "received-table trim failed"); }
                await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunCollectorAsync(ICollector collector, CancellationToken ct)
    {
        // Restart the collector with exponential backoff if it throws
        // (transient permission flips, OS service hiccups, etc.). A
        // dead-and-stayed-dead collector silently amputates a sensor —
        // the kernel sees nothing instead of a dropout signal — so the
        // failure is surfaced AND we keep retrying.
        var backoff = TimeSpan.FromSeconds(5);
        var maxBackoff = TimeSpan.FromMinutes(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in collector.StreamAsync(ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    await _outbox.EnqueueAsync(ev.Surface, ev.PayloadJson, ev.EmittedAt, ct: ct).ConfigureAwait(false);
                    await _health.RecordSuccessAsync(collector.Surface, ct).ConfigureAwait(false);
                }
                // Stream completed cleanly (no permission, no work to do) — don't restart.
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "collector {Surface} crashed; restarting in {Backoff}", collector.Surface, backoff);
                var payload = JsonSerializer.Serialize(new
                {
                    collector = collector.Surface,
                    error = ex.GetType().FullName,
                    message = ex.Message,
                });
                try
                {
                    await _outbox.EnqueueAsync("client.collector_failure", payload, DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);
                    await _health.RecordErrorAsync(collector.Surface, ex.Message, ct).ConfigureAwait(false);
                }
                catch
                {
                    // outbox is unhappy; swallow — the next collector heartbeat will surface it.
                }
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // Unsubscribe before disposing the outbox: SSE is constructor-
        // injected and outlives the host in tests / restart paths, so
        // leaking these handlers re-enters a torn-down renderer/outbox.
        _sse.OnDeliver -= DispatchDeliverAsync;
        _sse.OnCancel -= _onCancelHandler;
        return _outbox.DisposeAsync();
    }
}
