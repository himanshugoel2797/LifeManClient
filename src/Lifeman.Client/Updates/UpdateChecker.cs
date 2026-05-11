using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Renderers;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Updates;

public sealed record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("download_url")] string? DownloadUrl,
    [property: JsonPropertyName("notes")] string? Notes);

public sealed class UpdateCheckerOptions
{
    /// CLIENT_DESIGN.md §"Distribution & updates": "Client polls weekly".
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromDays(7);
    /// Initial delay before the first check, so a freshly-paired client
    /// doesn't fire a noisy update prompt before the user has even seen
    /// it run.
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(15);
}

/// Polls `/api/system/client-updates/<platform>` for a newer build. When
/// found, raises a synthesized `OutputDeliver` through the renderer with
/// `category=alert urgency=soft` so the user sees a normal toast/notification
/// rather than a custom UI surface.
///
/// Server endpoint isn't live yet — see docs/PARENT_REPO_REQUESTS.md. This
/// checker treats 404 / DNS failure as "no update", logs at debug, and
/// continues polling so the moment the endpoint ships everything just works.
public sealed class UpdateChecker
{
    private readonly LifemanHttpClient _http;
    private readonly IRenderer _renderer;
    private readonly string _platform;
    private readonly string _currentVersion;
    private readonly UpdateCheckerOptions _options;
    private readonly ILogger<UpdateChecker> _logger;
    private string? _lastNotifiedVersion;

    public UpdateChecker(
        LifemanHttpClient http,
        IRenderer renderer,
        string platform,
        string currentVersion,
        UpdateCheckerOptions? options = null,
        ILogger<UpdateChecker>? logger = null)
    {
        _http = http;
        _renderer = renderer;
        _platform = platform;
        _currentVersion = currentVersion;
        _options = options ?? new UpdateCheckerOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateChecker>.Instance;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(_options.InitialDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await CheckOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "update check failed (transient)");
            }

            try { await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// Single check pass. Public so the head can call on reconnect.
    public async Task<UpdateInfo?> CheckOnceAsync(CancellationToken ct)
    {
        UpdateInfo? info;
        try
        {
            using var req = await _http.CreateAuthedRequestAsync(
                HttpMethod.Get, $"api/system/client-updates/{_platform}", ct).ConfigureAwait(false);
            using var resp = await _http.Raw.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("update endpoint returned {Status}", resp.StatusCode);
                return null;
            }
            info = await resp.Content.ReadFromJsonAsync<UpdateInfo>(LifemanJson.Options, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { return null; }

        if (info is null || string.IsNullOrWhiteSpace(info.Version)) return null;
        if (!IsNewer(info.Version, _currentVersion)) return null;
        if (string.Equals(info.Version, _lastNotifiedVersion, StringComparison.Ordinal)) return info;

        await NotifyAsync(info, ct).ConfigureAwait(false);
        _lastNotifiedVersion = info.Version;
        return info;
    }

    private Task NotifyAsync(UpdateInfo info, CancellationToken ct)
    {
        // Synthetic delivery — no kernel involvement. The output_id is
        // namespaced so the renderer's dedup table doesn't collide with
        // server-issued IDs and so a repeat notify across restarts replaces
        // (not stacks) the prior toast.
        var deliver = new OutputDeliver(
            OutputId: $"client.update:{info.Version}",
            DeliveryId: $"client.update:{info.Version}",
            DeviceId: "self",
            Category: "alert",
            Urgency: "soft",
            Content: new OutputContent(
                Title: $"Lifeman client update available ({info.Version})",
                Body: info.Notes ?? "A newer client build is available. Download and install when convenient."),
            Actions: Array.Empty<OutputAction>(),
            SourceTool: "client.update_checker",
            ExpiresAt: null,
            Seq: null);
        return _renderer.ShowAsync(deliver, ct);
    }

    /// Compare dotted versions ("1.2.3"). Trailing pre-release suffixes
    /// (`-rc1`, `+build42`) are stripped before parsing so a malformed tag
    /// doesn't crash the loop. Falls back to ordinal compare if either
    /// side won't parse.
    internal static bool IsNewer(string remote, string current)
    {
        if (TryParse(remote, out var r) && TryParse(current, out var c))
            return r > c;
        return string.CompareOrdinal(remote, current) > 0;
    }

    private static bool TryParse(string s, out Version v)
    {
        var trimmed = s.TrimStart('v', 'V');
        var dash = trimmed.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) trimmed = trimmed[..dash];
        return Version.TryParse(trimmed, out v!);
    }
}
