using System.Net.Http.Headers;
using Lifeman.Client.Config;

namespace Lifeman.Client.Net;

/// Lifeman's HTTP client. Wraps a configured `HttpClient` and resolves
/// every relative path against the paired server's base URL at call
/// time, so a re-pair against a different kernel takes effect without
/// reconstructing the client.
///
/// Auth is attached by the inner `DeviceTokenHandler` (`DelegatingHandler`),
/// not by the caller — there is no path that bypasses auth except the
/// explicit "no token" pairing flow, which uses a vanilla `HttpClient`.
public sealed class LifemanHttpClient
{
    private readonly HttpClient _http;
    private readonly IConfigStore _config;

    public LifemanHttpClient(HttpClient http, IConfigStore config)
    {
        _http = http;
        _config = config;
    }

    /// Resolve a path against the configured server base URL. Throws if
    /// pairing has not completed.
    public async Task<Uri> BuildUriAsync(string relativePath, CancellationToken ct)
    {
        var baseUrl = await _config.GetAsync(ConfigKeys.ServerBaseUrl, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Server URL not configured — complete pairing first.");
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relativePath.TrimStart('/'));
    }

    /// Build, send, and return the response. The bearer token is attached
    /// by the inner `DeviceTokenHandler` — callers don't need to thread
    /// the token through manually.
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, await BuildUriAsync(relativePath, ct).ConfigureAwait(false));
        if (content is not null) req.Content = content;
        return await _http.SendAsync(req, completion, ct).ConfigureAwait(false);
    }

    /// Send a pre-built request. The handler chain attaches auth; the
    /// URI is left untouched, so callers should already have resolved it
    /// via `BuildUriAsync`.
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
        => _http.SendAsync(request, completion, ct);
}

/// Attaches `Authorization: Bearer <device-token>` to every outgoing
/// request, reading the token from `IConfigStore` at send time so a
/// re-pair takes effect without reconstructing handlers.
public sealed class DeviceTokenHandler : DelegatingHandler
{
    private readonly IConfigStore _config;

    public DeviceTokenHandler(IConfigStore config) : base(new HttpClientHandler())
    {
        _config = config;
    }

    public DeviceTokenHandler(IConfigStore config, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _config = config;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization is null)
        {
            var token = await _config.GetAsync(ConfigKeys.DeviceToken, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}
