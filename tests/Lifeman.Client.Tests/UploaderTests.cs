using System.Net;
using System.Net.Http.Json;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;

namespace Lifeman.Client.Tests;

public sealed class UploaderTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteOutbox _outbox = null!;
    private InMemoryConfigStore _config = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lifeman-uploader-{Guid.NewGuid():N}.db");
        _outbox = new SqliteOutbox(_dbPath);
        await _outbox.InitAsync();
        _config = new InMemoryConfigStore();
        await _config.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        await _config.SetAsync(ConfigKeys.DeviceId, "dev123");
        await _config.SetAsync(ConfigKeys.DeviceToken, "tok");
    }

    public async Task DisposeAsync()
    {
        await _outbox.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private Uploader Build(StubHandler handler, UploaderOptions? options = null)
    {
        var http = new HttpClient(handler);
        var client = new LifemanHttpClient(http, _config);
        return new Uploader(_outbox, client, _config, options ?? new UploaderOptions
        {
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            IdlePollInterval = TimeSpan.Zero,
        });
    }

    [Fact]
    public async Task DrainOnce_AcksAllOnSuccess()
    {
        await _outbox.EnqueueAsync("phone.battery", "{\"level\":0.9}", DateTimeOffset.UtcNow);
        await _outbox.EnqueueAsync("phone.battery", "{\"level\":0.8}", DateTimeOffset.UtcNow);

        var handler = new StubHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("http://kernel.test/api/inputs/batch", req.RequestUri!.ToString());
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal("tok", req.Headers.Authorization.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new InputBatchResponse(new[]
            {
                new InputBatchItemResult(true, null, new InputAcceptedResponse("e1", Array.Empty<string>(), Array.Empty<string>(), false)),
                new InputBatchItemResult(true, null, new InputAcceptedResponse("e2", Array.Empty<string>(), Array.Empty<string>(), false)),
            })));
        });

        var uploader = Build(handler);
        uploader.SetNetworkProfile(isMetered: true); // batch hint = MaxBatchSize, so both flow in one batch
        var drained = await uploader.DrainOnceAsync(default);

        Assert.Equal(2, drained);
        Assert.Equal(0, await _outbox.CountAsync());
    }

    [Fact]
    public async Task DrainOnce_LeavesRowsOn5xx()
    {
        await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var uploader = Build(handler);
        var drained = await uploader.DrainOnceAsync(default);

        Assert.Equal(0, drained);
        Assert.Equal(1, await _outbox.CountAsync());
        var batch = await _outbox.PeekAsync(1);
        Assert.Equal(1, batch[0].Attempts);
    }

    [Fact]
    public async Task DrainOnce_DropsAfterMaxAttempts()
    {
        await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);

        var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new InputBatchResponse(new[]
        {
            new InputBatchItemResult(false, "BadRequest: malformed", null),
        }))));
        var uploader = Build(handler, new UploaderOptions
        {
            MaxAttempts = 2,
            InitialBackoff = TimeSpan.Zero,
            MaxBackoff = TimeSpan.Zero,
            IdlePollInterval = TimeSpan.Zero,
        });

        // First pass: 1st failure → attempts becomes 1 (non-permanent).
        await uploader.DrainOnceAsync(default);
        Assert.Equal(1, await _outbox.CountAsync());

        // Second pass: attempts is now 1, so next failure would hit MaxAttempts=2 → dropped as permanent.
        var drained = await uploader.DrainOnceAsync(default);
        Assert.Equal(1, drained);
        Assert.Equal(0, await _outbox.CountAsync());
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body)
        => new(status) { Content = JsonContent.Create(body, options: LifemanJson.Options) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _fn(request, ct);
    }
}
