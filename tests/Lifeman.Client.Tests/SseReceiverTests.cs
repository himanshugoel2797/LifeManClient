using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lifeman.Client.Config;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;

namespace Lifeman.Client.Tests;

/// We test the inner StreamAsync / CatchUpAsync methods directly (via
/// InternalsVisibleTo) rather than wrestling with the public RunAsync
/// reconnect loop in tests. Reconnect-loop behaviour is exercised by the
/// end-to-end smoke test against a real kernel.
public sealed class SseReceiverTests
{
    [Fact]
    public async Task StreamAsync_Parses_OutputDeliver()
    {
        var body = "event: output.deliver\ndata: {\"output_id\":\"o1\",\"delivery_id\":\"d1\",\"device_id\":\"dev\",\"category\":\"alert\",\"urgency\":\"urgent\",\"content\":{\"title\":\"hi\",\"body\":\"there\"},\"actions\":[],\"source_tool\":null,\"expires_at\":null,\"_seq\":1}\n\n";
        var (sse, _) = BuildReceiver(body);

        OutputDeliver? received = null;
        sse.OnDeliver += (d, _) => { received = d; return Task.CompletedTask; };

        await sse.StreamAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("o1", received!.OutputId);
        Assert.Equal("urgent", received.Urgency);
        Assert.Equal("hi", received.Content.Title);
    }

    [Fact]
    public async Task StreamAsync_Parses_OutputCancel()
    {
        var body = "event: output.cancel\ndata: {\"output_id\":\"o1\",\"delivery_id\":\"d1\",\"device_id\":\"dev\",\"channel\":\"device:dev\",\"_seq\":2}\n\n";
        var (sse, _) = BuildReceiver(body);

        OutputCancel? received = null;
        sse.OnCancel += (c, _) => { received = c; return Task.CompletedTask; };

        await sse.StreamAsync(CancellationToken.None);
        Assert.NotNull(received);
        Assert.Equal("o1", received!.OutputId);
    }

    [Fact]
    public async Task StreamAsync_Reports_SseDropped()
    {
        var body = "event: sse.dropped\ndata: {\"count\":7}\n\n";
        var (sse, _) = BuildReceiver(body);

        int? count = null;
        sse.OnDropped += (n, _) => { count = n; return Task.CompletedTask; };

        await sse.StreamAsync(CancellationToken.None);
        Assert.Equal(7, count);
    }

    [Fact]
    public async Task StreamAsync_Ignores_Comments()
    {
        var body = ": keep-alive\n\nevent: output.deliver\ndata: {\"output_id\":\"o1\",\"delivery_id\":\"d1\",\"device_id\":\"d\",\"category\":\"c\",\"urgency\":\"u\",\"content\":{\"title\":\"t\",\"body\":\"b\"},\"actions\":[],\"source_tool\":null,\"expires_at\":null,\"_seq\":1}\n\n";
        var (sse, _) = BuildReceiver(body);

        var hits = 0;
        sse.OnDeliver += (_, _) => { hits++; return Task.CompletedTask; };

        await sse.StreamAsync(CancellationToken.None);
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task CatchUpAsync_UrlEncodesCursor_AndAdvancesIt()
    {
        var config = new InMemoryConfigStore();
        await config.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        await config.SetAsync(ConfigKeys.DeviceToken, "tok");
        // cursor has a `+` from the timezone offset — must be encoded as
        // %2B or the server's parser decodes it as a space.
        await config.SetAsync(ConfigKeys.PendingCursor, "2026-05-10T22:03:20.487295+00:00");

        Uri? observed = null;
        var handler = new StubHandler((req, _) =>
        {
            observed = req.RequestUri;
            var resp = new PendingOutputsResponse(new[]
            {
                new OutputDeliver("o1","d1","dev","alert","urgent",
                    new OutputContent("t","b"), Array.Empty<OutputAction>(), null, null, 1),
            }, Cursor: "2026-05-10T22:03:25.000000+00:00");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(resp, options: LifemanJson.Options),
            });
        });

        var http = new HttpClient(handler);
        var sse = new SseReceiver(new LifemanHttpClient(http, config), config);
        var hits = 0;
        sse.OnDeliver += (_, _) => { hits++; return Task.CompletedTask; };

        await sse.CatchUpAsync(CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Contains("since=2026-05-10T22%3A03%3A20.487295%2B00%3A00", observed!.Query);
        Assert.Equal(1, hits);
        Assert.Equal("2026-05-10T22:03:25.000000+00:00",
            await config.GetAsync(ConfigKeys.PendingCursor));
    }

    private static (SseReceiver sse, InMemoryConfigStore config) BuildReceiver(string streamBody)
    {
        var config = new InMemoryConfigStore();
        config.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test").AsTask().Wait();
        config.SetAsync(ConfigKeys.DeviceToken, "tok").AsTask().Wait();
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(streamBody, Encoding.UTF8, "text/event-stream"),
        }));
        var http = new HttpClient(handler);
        return (new SseReceiver(new LifemanHttpClient(http, config), config), config);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _fn(request, ct);
    }
}
