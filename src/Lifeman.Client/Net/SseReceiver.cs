using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Net;

public sealed class SseReceiverOptions
{
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(2);
}

/// Long-lived `/events?token=…` consumer plus `/api/outputs/pending`
/// catch-up after every reconnect. The receiver does no rendering itself;
/// it forwards parsed events to handlers wired by the host.
public sealed class SseReceiver
{
    private readonly LifemanHttpClient _client;
    private readonly IConfigStore _config;
    private readonly SseReceiverOptions _options;
    private readonly ILogger<SseReceiver> _logger;

    public event Func<OutputDeliver, CancellationToken, Task>? OnDeliver;
    public event Func<OutputCancel, CancellationToken, Task>? OnCancel;
    public event Func<int, CancellationToken, Task>? OnDropped;

    public SseReceiver(
        LifemanHttpClient client,
        IConfigStore config,
        SseReceiverOptions? options = null,
        ILogger<SseReceiver>? logger = null)
    {
        _client = client;
        _config = config;
        _options = options ?? new SseReceiverOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SseReceiver>.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = _options.InitialReconnectDelay;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CatchUpAsync(ct).ConfigureAwait(false);
                await StreamAsync(ct).ConfigureAwait(false);
                // Clean exit ⇒ server closed; reset backoff before reconnecting.
                delay = _options.InitialReconnectDelay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSE stream error; reconnecting in {Delay}", delay);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromTicks(Math.Min(_options.MaxReconnectDelay.Ticks, delay.Ticks * 2));
            }
        }
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        var token = await _config.GetAsync(ConfigKeys.DeviceToken, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No device token — pair before starting SSE.");

        // EventSource semantics: token goes in the query string, not a header.
        var url = await _client.BuildUriAsync($"events?token={Uri.EscapeDataString(token)}", ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = await _client.Raw.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("SSE returned 401 — re-pair required.");
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataBuf = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break; // server closed

            if (line.Length == 0)
            {
                if (dataBuf.Length > 0 && eventName is not null)
                {
                    var data = dataBuf.ToString();
                    await DispatchAsync(eventName, data, ct).ConfigureAwait(false);
                }
                eventName = null;
                dataBuf.Clear();
                continue;
            }

            if (line.StartsWith(':')) continue; // SSE comment / keep-alive
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var field = line[..colon];
            var value = colon + 1 < line.Length && line[colon + 1] == ' '
                ? line[(colon + 2)..]
                : line[(colon + 1)..];
            switch (field)
            {
                case "event": eventName = value; break;
                case "data":
                    if (dataBuf.Length > 0) dataBuf.Append('\n');
                    dataBuf.Append(value);
                    break;
                // We don't honor `id:` for replay — the kernel exposes
                // /api/outputs/pending with a server-side cursor which is
                // more durable than the in-memory ring buffer.
            }
        }
    }

    private async Task DispatchAsync(string eventName, string data, CancellationToken ct)
    {
        try
        {
            switch (eventName)
            {
                case "output.deliver":
                    var deliver = JsonSerializer.Deserialize<OutputDeliver>(data, LifemanJson.Options);
                    if (deliver is not null)
                    {
                        if (OnDeliver is not null) await OnDeliver(deliver, ct).ConfigureAwait(false);
                        await UpdateCursorAsync(ConfigKeys.PendingCursor, deliver.ExpiresAt, ct).ConfigureAwait(false);
                    }
                    break;
                case "output.cancel":
                    var cancel = JsonSerializer.Deserialize<OutputCancel>(data, LifemanJson.Options);
                    if (cancel is not null && OnCancel is not null)
                        await OnCancel(cancel, ct).ConfigureAwait(false);
                    break;
                case "sse.dropped":
                    using (var doc = JsonDocument.Parse(data))
                    {
                        if (doc.RootElement.TryGetProperty("count", out var c)
                            && OnDropped is not null)
                            await OnDropped(c.GetInt32(), ct).ConfigureAwait(false);
                    }
                    break;
                case "sse.sync":
                    // boundary between replay and live; nothing to do yet
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to dispatch SSE event '{EventName}': {Data}", eventName, data);
        }
    }

    private async Task CatchUpAsync(CancellationToken ct)
    {
        var cursor = await _config.GetAsync(ConfigKeys.PendingCursor, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(cursor)) return;

        // URL-encode the cursor — the `+` in the timezone offset becomes a
        // space otherwise and pagination silently skips an event.
        var path = $"api/outputs/pending?since={Uri.EscapeDataString(cursor)}";
        using var req = await _client.CreateAuthedRequestAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        using var resp = await _client.Raw.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("pending fetch returned 401 — re-pair required.");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PendingOutputsResponse>(LifemanJson.Options, ct).ConfigureAwait(false);
        if (body is null) return;

        foreach (var ev in body.Events)
        {
            if (OnDeliver is not null) await OnDeliver(ev, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(body.Cursor))
            await _config.SetAsync(ConfigKeys.PendingCursor, body.Cursor, ct).ConfigureAwait(false);
    }

    private async Task UpdateCursorAsync(string key, DateTimeOffset? value, CancellationToken ct)
    {
        if (value is null) return;
        await _config.SetAsync(key, value.Value.ToString("O"), ct).ConfigureAwait(false);
    }
}
