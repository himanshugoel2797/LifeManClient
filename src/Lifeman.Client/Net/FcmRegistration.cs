using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Net;

/// Cellular-friendly push transport. CLIENT_DESIGN.md §"Inbound:
/// output delivery" defers FCM to "later" (deliberately) — keeping a
/// long-lived SSE connection open over a metered radio shreds battery,
/// so the server should only nudge the device via FCM and the client
/// drains via `/api/outputs/pending?since=…` after waking.
///
/// This class registers the device's FCM token with the server. The
/// kernel-side endpoint (`POST /api/devices/push-token`) and the
/// FCM-publisher (kernel → FCM → device wake → /pending fetch) are
/// **not yet shipped** — see docs/PARENT_REPO_REQUESTS.md. The
/// platform-specific token acquisition (Firebase.Messaging on Android)
/// is also out of scope here; the head supplies the token via
/// `RegisterTokenAsync(token)` once it has one.
public sealed class FcmRegistration
{
    private readonly LifemanHttpClient _http;
    private readonly IConfigStore _config;
    private readonly ILogger<FcmRegistration> _logger;

    public FcmRegistration(
        LifemanHttpClient http,
        IConfigStore config,
        ILogger<FcmRegistration>? logger = null)
    {
        _http = http;
        _config = config;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<FcmRegistration>.Instance;
    }

    /// Sends the FCM token to the kernel. Idempotent: a no-op if the
    /// token is unchanged from the last registered value (cached in the
    /// config store under `push.fcm_token`). Treats 404 / 501 from the
    /// server as "endpoint not yet implemented" — logs at debug, skips
    /// silently. The next reconnect will retry.
    public async Task RegisterTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        var prior = await _config.GetAsync(ConfigKeys.PushFcmToken, ct).ConfigureAwait(false);
        if (string.Equals(prior, token, StringComparison.Ordinal))
        {
            _logger.LogDebug("fcm token unchanged; skipping re-registration");
            return;
        }

        try
        {
            using var req = await _http.CreateAuthedRequestAsync(
                HttpMethod.Post, "api/devices/push-token", ct).ConfigureAwait(false);
            req.Content = JsonContent.Create(new PushTokenRequest("fcm", token), options: LifemanJson.Options);
            using var resp = await _http.Raw.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
            {
                _logger.LogDebug("server has no push-token endpoint yet (status {Status})", resp.StatusCode);
                return;
            }
            resp.EnsureSuccessStatusCode();
            await _config.SetAsync(ConfigKeys.PushFcmToken, token, ct).ConfigureAwait(false);
            _logger.LogInformation("fcm token registered with kernel");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "fcm token registration network error (will retry)");
        }
    }

    private sealed record PushTokenRequest(
        [property: JsonPropertyName("transport")] string Transport,
        [property: JsonPropertyName("token")] string Token);
}
