using System.Net;
using System.Net.Http.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Renderers;
using Lifeman.Client.Updates;

namespace Lifeman.Client.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("v2.0.0", "1.99.0", true)]
    [InlineData("1.4.0-rc1", "1.3.99", true)]
    public void IsNewer_DottedSemver(string remote, string current, bool expected)
        => Assert.Equal(expected, UpdateChecker.IsNewer(remote, current));

    [Fact]
    public async Task CheckOnce_ReturnsNullOn404()
    {
        var (config, http) = await BuildAsync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var renderer = new RecordingRenderer();
        var checker = new UpdateChecker(http, renderer, "windows", "1.0.0");

        var info = await checker.CheckOnceAsync(default);
        Assert.Null(info);
        Assert.Empty(renderer.Shown);
    }

    [Fact]
    public async Task CheckOnce_RendersWhenNewer()
    {
        var update = new UpdateInfo(Version: "1.5.0", Sha256: "abc", DownloadUrl: "http://k/dl", Notes: "fixes things");
        var (config, http) = await BuildAsync(_ => JsonResponse(HttpStatusCode.OK, update));
        var renderer = new RecordingRenderer();
        var checker = new UpdateChecker(http, renderer, "windows", "1.0.0");

        var info = await checker.CheckOnceAsync(default);
        Assert.Equal("1.5.0", info?.Version);
        var shown = Assert.Single(renderer.Shown);
        Assert.Equal("client.update:1.5.0", shown.OutputId);
        Assert.Equal("alert", shown.Category);
        Assert.Equal("soft", shown.Urgency);
        Assert.Contains("1.5.0", shown.Content.Title);
    }

    [Fact]
    public async Task CheckOnce_DoesNotRenderWhenSameOrOlder()
    {
        var update = new UpdateInfo(Version: "1.0.0", Sha256: null, DownloadUrl: null, Notes: null);
        var (config, http) = await BuildAsync(_ => JsonResponse(HttpStatusCode.OK, update));
        var renderer = new RecordingRenderer();
        var checker = new UpdateChecker(http, renderer, "windows", "1.0.0");

        await checker.CheckOnceAsync(default);
        Assert.Empty(renderer.Shown);
    }

    [Fact]
    public async Task CheckOnce_DoesNotRepeatNotificationForSameVersion()
    {
        var update = new UpdateInfo(Version: "2.0.0", Sha256: null, DownloadUrl: null, Notes: null);
        var (config, http) = await BuildAsync(_ => JsonResponse(HttpStatusCode.OK, update));
        var renderer = new RecordingRenderer();
        var checker = new UpdateChecker(http, renderer, "windows", "1.0.0");

        await checker.CheckOnceAsync(default);
        await checker.CheckOnceAsync(default);
        await checker.CheckOnceAsync(default);
        Assert.Single(renderer.Shown);
    }

    private static async Task<(InMemoryConfigStore config, LifemanHttpClient http)> BuildAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var config = new InMemoryConfigStore();
        await config.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        await config.SetAsync(ConfigKeys.DeviceToken, "tok");
        var handler = new StubHandler(respond);
        var http = new LifemanHttpClient(new HttpClient(handler), config);
        return (config, http);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body)
        => new(status) { Content = JsonContent.Create(body, options: LifemanJson.Options) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_fn(request));
    }

    private sealed class RecordingRenderer : IRenderer
    {
        public List<OutputDeliver> Shown { get; } = new();
        public Task ShowAsync(OutputDeliver deliver, CancellationToken ct) { Shown.Add(deliver); return Task.CompletedTask; }
        public Task DismissAsync(string outputId, CancellationToken ct) => Task.CompletedTask;
    }
}
