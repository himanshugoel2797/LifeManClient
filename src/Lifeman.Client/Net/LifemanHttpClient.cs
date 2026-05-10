using System.Net.Http.Headers;
using Lifeman.Client.Config;

namespace Lifeman.Client.Net;

/// Wraps a base HttpClient and attaches the device bearer token (read from
/// IConfigStore at request time so token revocation/re-pair takes effect
/// without recreating the client).
public sealed class LifemanHttpClient
{
    private readonly HttpClient _http;
    private readonly IConfigStore _config;

    public LifemanHttpClient(HttpClient http, IConfigStore config)
    {
        _http = http;
        _config = config;
    }

    public HttpClient Raw => _http;

    public async Task<Uri> BuildUriAsync(string relativePath, CancellationToken ct)
    {
        var baseUrl = await _config.GetAsync(ConfigKeys.ServerBaseUrl, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Server URL not configured — complete pairing first.");
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relativePath.TrimStart('/'));
    }

    public async Task<HttpRequestMessage> CreateAuthedRequestAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, await BuildUriAsync(relativePath, ct).ConfigureAwait(false));
        var token = await _config.GetAsync(ConfigKeys.DeviceToken, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }
}
