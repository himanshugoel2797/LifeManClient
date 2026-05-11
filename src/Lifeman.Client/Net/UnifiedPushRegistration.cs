using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Net;

/// Cellular-friendly push transport via UnifiedPush. CLIENT_DESIGN.md
/// §"Inbound: output delivery" defers wake-only push to "later"
/// (deliberately) — keeping a long-lived SSE connection open over a
/// metered radio shreds battery, so the server should only nudge the
/// device via push and the client drains via
/// `/api/outputs/pending?since=…` after waking.
///
/// Why UnifiedPush instead of FCM: UnifiedPush is a Google-free push
/// spec (Web Push / RFC 8030 under the hood). The device picks a
/// distributor (ntfy, NextPush, FCM-UP, …); the distributor hands the
/// app an HTTPS endpoint; the server POSTs wake messages to that
/// endpoint. No Firebase project, no service-account secrets, no
/// Play Services dependency. Works on de-Googled phones and the
/// kernel never has to talk to a Google API.
///
/// This class registers the device's UnifiedPush endpoint with the
/// server. The kernel-side endpoint (`POST /api/devices/push-token`)
/// and the push-publisher (kernel → endpoint URL → device wake →
/// /pending fetch) are **not yet shipped** — see
/// docs/PARENT_REPO_REQUESTS.md. The platform-specific endpoint
/// acquisition (UnifiedPush distributor binding on Android) is also
/// out of scope here; the head supplies the endpoint via
/// `RegisterEndpointAsync(endpoint)` once it has one.
public sealed class UnifiedPushRegistration
{
    private readonly LifemanHttpClient _http;
    private readonly IConfigStore _config;
    private readonly ILogger<UnifiedPushRegistration> _logger;

    public UnifiedPushRegistration(
        LifemanHttpClient http,
        IConfigStore config,
        ILogger<UnifiedPushRegistration>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<UnifiedPushRegistration>.Instance;
    }

    /// Sends the UnifiedPush endpoint to the kernel. Idempotent: a no-op
    /// if the endpoint is unchanged from the last registered value
    /// (cached in the config store under `push.unifiedpush_endpoint`).
    /// Treats 404 / 501 from the server as "endpoint not yet implemented"
    /// — logs at debug, skips silently. The next reconnect will retry.
    public async Task RegisterEndpointAsync(string endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var prior = await _config.GetAsync(ConfigKeys.PushUnifiedPushEndpoint, ct).ConfigureAwait(false);
        if (string.Equals(prior, endpoint, StringComparison.Ordinal))
        {
            _logger.LogDebug("unifiedpush endpoint unchanged; skipping re-registration");
            return;
        }

        try
        {
            using var content = JsonContent.Create(new PushTokenRequest("unifiedpush", endpoint), options: LifemanJson.Options);
            using var resp = await _http.SendAsync(HttpMethod.Post, "api/devices/push-token", content, ct: ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            {
                _logger.LogDebug("server has no push-token endpoint yet (status {Status})", resp.StatusCode);
                return;
            }
            resp.EnsureSuccessStatusCode();
            await _config.SetAsync(ConfigKeys.PushUnifiedPushEndpoint, endpoint, ct).ConfigureAwait(false);
            _logger.LogInformation("unifiedpush endpoint registered with kernel");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "unifiedpush endpoint registration network error (will retry)");
        }
    }

    /// Tells the kernel to forget our push endpoint — used when the
    /// distributor is uninstalled or the user disables push. Best-effort:
    /// network errors are swallowed; clearing the cached endpoint locally
    /// is what makes the next re-registration actually re-POST.
    public async Task UnregisterAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.SendAsync(HttpMethod.Delete, "api/devices/push-token", ct: ct).ConfigureAwait(false);
            // 200/204/404/501 all treated as success — see method comment.
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "unifiedpush endpoint unregister network error (ignored)");
        }
        await _config.DeleteAsync(ConfigKeys.PushUnifiedPushEndpoint, ct).ConfigureAwait(false);
    }

    /// Server-side wire shape. The `token` field name is the kernel's
    /// transport-agnostic slot — for UnifiedPush it carries the endpoint
    /// URL the kernel will POST wake messages to.
    private sealed record PushTokenRequest(
        [property: JsonPropertyName("transport")] string Transport,
        [property: JsonPropertyName("token")] string Token);
}
