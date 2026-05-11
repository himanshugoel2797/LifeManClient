using System.Net;
using System.Net.Http.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Outbox;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Net;

public sealed class UploaderOptions
{
    /// Cap per /api/inputs/batch request; server enforces 200.
    public int MaxBatchSize { get; init; } = 50;
    /// Initial delay after a failed batch.
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(2);
    /// Max delay between retries.
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    /// After this many consecutive attempts on the same row, mark it
    /// permanent-failed and delete to prevent poison-pill stalls.
    public int MaxAttempts { get; init; } = 20;
    /// Idle delay between drain passes when the outbox is empty.
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// Drains the outbox into /api/inputs/batch with exponential backoff.
/// Adaptive batch size hint (cellular vs Wi-Fi) comes from the platform
/// head — call SetNetworkProfile to bias batches small (Wi-Fi, prefer
/// freshness) or large (metered, amortise radio wakes).
public sealed class Uploader
{
    private readonly IOutbox _outbox;
    private readonly LifemanHttpClient _client;
    private readonly IConfigStore _config;
    private readonly UploaderOptions _options;
    private readonly ILogger<Uploader> _logger;
    private TimeSpan _currentBackoff;
    private int _batchHint;

    public Uploader(
        IOutbox outbox,
        LifemanHttpClient client,
        IConfigStore config,
        UploaderOptions? options = null,
        ILogger<Uploader>? logger = null)
    {
        _outbox = outbox;
        _client = client;
        _config = config;
        _options = options ?? new UploaderOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Uploader>.Instance;
        _currentBackoff = _options.InitialBackoff;
        _batchHint = 1; // start gentle until we see the connection
    }

    /// 1 on Wi-Fi (prefer freshness), up to MaxBatchSize on cellular
    /// (amortise radio wakes). Called by the platform head when the
    /// network state changes.
    public void SetNetworkProfile(bool isMetered)
    {
        _batchHint = isMetered ? _options.MaxBatchSize : 1;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var drained = await DrainOnceAsync(ct).ConfigureAwait(false);
                if (drained == 0)
                {
                    await Task.Delay(_options.IdlePollInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "uploader drain pass failed; backing off {Backoff}", _currentBackoff);
                await Task.Delay(_currentBackoff, ct).ConfigureAwait(false);
                _currentBackoff = TimeSpan.FromTicks(Math.Min(_options.MaxBackoff.Ticks, _currentBackoff.Ticks * 2));
            }
        }
    }

    /// One drain pass. Returns the count of events that left the outbox
    /// (acked or permanently dropped). Public so tests can call without
    /// the loop.
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var batch = await _outbox.PeekAsync(_batchHint, ct).ConfigureAwait(false);
        if (batch.Count == 0) return 0;

        var source = await BuildSourceAsync(ct).ConfigureAwait(false);
        var payload = new InputBatchRequest(
            batch.Select(e => e.ToInputEvent(source)).ToArray());

        using var content = JsonContent.Create(payload, options: LifemanJson.Options);
        using var resp = await _client.SendAsync(HttpMethod.Post, "api/inputs/batch", content, ct: ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            await HandleBatchFailureAsync(batch, resp, ct).ConfigureAwait(false);
            return 0;
        }

        var body = await resp.Content.ReadFromJsonAsync<InputBatchResponse>(LifemanJson.Options, ct).ConfigureAwait(false);
        if (body is null || body.Results.Count != batch.Count)
        {
            await _outbox.FailAsync(batch.Select(e => e.Id).ToArray(), "malformed batch response", permanent: false, ct).ConfigureAwait(false);
            return 0;
        }

        var acked = new List<long>();
        var failed = new List<long>();
        var permFailed = new List<long>();
        string? lastError = null;
        for (var i = 0; i < batch.Count; i++)
        {
            var result = body.Results[i];
            var entry = batch[i];
            if (result.Ok)
            {
                acked.Add(entry.Id);
            }
            else if (entry.Attempts + 1 >= _options.MaxAttempts)
            {
                _logger.LogWarning("dropping outbox entry {Id} (surface {Surface}) after {Attempts} attempts: {Error}",
                    entry.Id, entry.Surface, entry.Attempts + 1, result.Error);
                permFailed.Add(entry.Id);
            }
            else
            {
                failed.Add(entry.Id);
                lastError = result.Error;
            }
        }

        await _outbox.AckAsync(acked, ct).ConfigureAwait(false);
        if (permFailed.Count > 0)
            await _outbox.FailAsync(permFailed, "max attempts exceeded", permanent: true, ct).ConfigureAwait(false);
        if (failed.Count > 0)
            await _outbox.FailAsync(failed, lastError ?? "batch slot failure", permanent: false, ct).ConfigureAwait(false);

        _currentBackoff = _options.InitialBackoff;
        return acked.Count + permFailed.Count;
    }

    private async Task HandleBatchFailureAsync(IReadOnlyList<OutboxEntry> batch, HttpResponseMessage resp, CancellationToken ct)
    {
        var status = resp.StatusCode;
        var ids = batch.Select(e => e.Id).ToArray();
        var error = $"HTTP {(int)status} {status}";

        // 401 → token revoked. Don't churn. Surface to caller via config
        // flag; the head should drop into re-pair UI. Leave events in outbox
        // WITHOUT incrementing attempts — otherwise a long re-pair window
        // ticks every queued row past MaxAttempts and we silently delete
        // legitimate data the moment the user re-pairs.
        if (status == HttpStatusCode.Unauthorized)
        {
            _logger.LogError("uploader received 401 — device token may be revoked. Pause and re-pair.");
            await _config.SetAsync(ConfigKeys.RepairRequired, "1", ct).ConfigureAwait(false);
            await Task.Delay(_options.MaxBackoff, ct).ConfigureAwait(false);
            return;
        }

        // 413 → server batch cap exceeded. Halve the batch hint and retry next pass.
        // If we're already at one row per batch, the single payload itself is
        // oversized — no future retry will succeed, so drop it permanently
        // rather than spinning forever on the same poison row.
        if (status == HttpStatusCode.RequestEntityTooLarge)
        {
            if (_batchHint <= 1)
            {
                _logger.LogError("dropping outbox entry {Id} (surface {Surface}): single payload exceeds server cap",
                    batch[0].Id, batch[0].Surface);
                await _outbox.FailAsync(ids, "single payload exceeds server batch cap", permanent: true, ct).ConfigureAwait(false);
                return;
            }
            _batchHint = Math.Max(1, _batchHint / 2);
            _logger.LogWarning("batch too large — dropping batch hint to {Hint}", _batchHint);
            await _outbox.FailAsync(ids, error, permanent: false, ct).ConfigureAwait(false);
            return;
        }

        await _outbox.FailAsync(ids, error, permanent: false, ct).ConfigureAwait(false);
        _logger.LogWarning("batch failed: {Error}; backing off {Backoff}", error, _currentBackoff);
        await Task.Delay(_currentBackoff, ct).ConfigureAwait(false);
        _currentBackoff = TimeSpan.FromTicks(Math.Min(_options.MaxBackoff.Ticks, _currentBackoff.Ticks * 2));
    }

    private async Task<string?> BuildSourceAsync(CancellationToken ct)
    {
        var id = await _config.GetAsync(ConfigKeys.DeviceId, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(id) ? null : $"device:{id}";
    }
}
