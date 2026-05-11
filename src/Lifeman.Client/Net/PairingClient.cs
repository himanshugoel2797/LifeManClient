using System.Net.Http.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;

namespace Lifeman.Client.Net;

/// Exchanges a pairing code for a per-device token via POST /api/auth/pair,
/// then persists the token + device id + server URL into the config store
/// (where the platform head wraps sensitive fields with the OS keystore).
public sealed class PairingClient
{
    private readonly HttpClient _http;
    private readonly IConfigStore _config;

    public PairingClient(HttpClient http, IConfigStore config)
    {
        _http = http;
        _config = config;
    }

    public async Task<PairResponse> PairAsync(
        string serverBaseUrl,
        string code,
        string name,
        string platform,
        DeviceCapabilities capabilities,
        CancellationToken ct = default)
    {
        var url = new Uri(new Uri(serverBaseUrl.TrimEnd('/') + "/"), "api/auth/pair");
        var req = new PairRequest(code, name, platform, capabilities);
        using var resp = await _http.PostAsJsonAsync(url, req, LifemanJson.Options, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var pair = await resp.Content.ReadFromJsonAsync<PairResponse>(LifemanJson.Options, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Pairing response was empty.");

        // Token is returned exactly once — store it before this method returns.
        await _config.SetAsync(ConfigKeys.ServerBaseUrl, serverBaseUrl, ct).ConfigureAwait(false);
        await _config.SetAsync(ConfigKeys.DeviceId, pair.DeviceId, ct).ConfigureAwait(false);
        await _config.SetAsync(ConfigKeys.DeviceName, pair.Name, ct).ConfigureAwait(false);
        await _config.SetAsync(ConfigKeys.DeviceToken, pair.Token, ct).ConfigureAwait(false);
        // Clear any stale 401 flag from a previous token now that we hold a fresh one.
        await _config.DeleteAsync(ConfigKeys.RepairRequired, ct).ConfigureAwait(false);
        return pair;
    }

    /// Parse a `lifeman://pair?host=<server-url>&code=<code>` URL into its
    /// components. The host param is the full base URL (e.g.
    /// `http://10.0.0.5:8390`), not just a hostname.
    public static (string ServerBaseUrl, string Code) ParsePairUrl(string pairUrl)
    {
        var uri = new Uri(pairUrl);
        if (!string.Equals(uri.Scheme, "lifeman", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a lifeman://pair?... URL.", nameof(pairUrl));
        var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var host = qs["host"] ?? throw new ArgumentException("Pair URL missing host param.", nameof(pairUrl));
        var code = qs["code"] ?? throw new ArgumentException("Pair URL missing code param.", nameof(pairUrl));
        return (host, code);
    }
}
