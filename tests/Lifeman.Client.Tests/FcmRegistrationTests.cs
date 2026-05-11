using System.Net;
using Lifeman.Client.Config;
using Lifeman.Client.Net;

namespace Lifeman.Client.Tests;

public sealed class FcmRegistrationTests
{
    [Fact]
    public async Task Register_PostsTokenAndCachesOnSuccess()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var fcm = new FcmRegistration(http, cfg);

        await fcm.RegisterTokenAsync("tok-abc-123");

        var req = Assert.Single(capture);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://kernel.test/api/devices/push-token", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("tok-abc-123", await cfg.GetAsync(ConfigKeys.PushFcmToken));
    }

    [Fact]
    public async Task Register_SkipsRePostForUnchangedToken()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var fcm = new FcmRegistration(http, cfg);

        await fcm.RegisterTokenAsync("same");
        await fcm.RegisterTokenAsync("same");
        await fcm.RegisterTokenAsync("same");

        Assert.Single(capture);
    }

    [Fact]
    public async Task Register_TreatsServerNotImplementedAsNoop()
    {
        // 404 / 501 = "endpoint not deployed yet". Don't cache the token
        // (we want to retry next reconnect) and don't throw.
        var (cfg, http, _) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var fcm = new FcmRegistration(http, cfg);

        await fcm.RegisterTokenAsync("tok");

        Assert.Null(await cfg.GetAsync(ConfigKeys.PushFcmToken));
    }

    [Fact]
    public async Task Register_IgnoresEmptyToken()
    {
        var (cfg, http, capture) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var fcm = new FcmRegistration(http, cfg);

        await fcm.RegisterTokenAsync("");
        await fcm.RegisterTokenAsync("   ");

        Assert.Empty(capture);
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
