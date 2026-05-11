using System.Net;
using System.Text.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Net;

namespace Lifeman.Client.Tests;

public sealed class UnifiedPushRegistrationTests
{
    [Fact]
    public async Task Register_PostsEndpointAndCachesOnSuccess()
    {
        string? capturedBody = null;
        var (cfg, http, capture) = await BuildAsync(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var push = new UnifiedPushRegistration(http, cfg);

        await push.RegisterEndpointAsync("https://ntfy.example.com/up?topic=abc");

        var req = Assert.Single(capture);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://kernel.test/api/devices/push-token", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("unifiedpush", doc.RootElement.GetProperty("transport").GetString());
        Assert.Equal("https://ntfy.example.com/up?topic=abc", doc.RootElement.GetProperty("token").GetString());
        Assert.Equal("https://ntfy.example.com/up?topic=abc", await cfg.GetAsync(ConfigKeys.PushUnifiedPushEndpoint));
    }

    [Fact]
    public async Task Register_SkipsRePostForUnchangedEndpoint()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var push = new UnifiedPushRegistration(http, cfg);

        await push.RegisterEndpointAsync("https://up.example/same");
        await push.RegisterEndpointAsync("https://up.example/same");
        await push.RegisterEndpointAsync("https://up.example/same");

        Assert.Single(capture);
    }

    [Fact]
    public async Task Register_TreatsServerNotImplementedAsNoop()
    {
        // 404 / 501 = "endpoint not deployed yet". Don't cache the endpoint
        // (we want to retry next reconnect) and don't throw.
        var (cfg, http, _) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var push = new UnifiedPushRegistration(http, cfg);

        await push.RegisterEndpointAsync("https://up.example/ep");

        Assert.Null(await cfg.GetAsync(ConfigKeys.PushUnifiedPushEndpoint));
    }

    [Fact]
    public async Task Register_IgnoresEmptyEndpoint()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var push = new UnifiedPushRegistration(http, cfg);

        await push.RegisterEndpointAsync("");
        await push.RegisterEndpointAsync("   ");

        Assert.Empty(capture);
    }

    [Fact]
    public async Task Unregister_DeletesServerSideAndClearsCache()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await cfg.SetAsync(ConfigKeys.PushUnifiedPushEndpoint, "https://up.example/ep");
        var push = new UnifiedPushRegistration(http, cfg);

        await push.UnregisterAsync();

        var req = Assert.Single(capture);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Equal("http://kernel.test/api/devices/push-token", req.RequestUri!.ToString());
        Assert.Null(await cfg.GetAsync(ConfigKeys.PushUnifiedPushEndpoint));
    }

    private static async Task<(InMemoryConfigStore cfg, LifemanHttpClient http, List<HttpRequestMessage> capture)> BuildAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var cfg = new InMemoryConfigStore();
        await cfg.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        await cfg.SetAsync(ConfigKeys.DeviceToken, "device-tok");
        var capture = new List<HttpRequestMessage>();
        var handler = new StubHandler(req => { capture.Add(req); return respond(req); });
        var http = new LifemanHttpClient(new HttpClient(handler), cfg);
        return (cfg, http, capture);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_fn(request));
    }
}
