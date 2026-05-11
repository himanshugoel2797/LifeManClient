using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lifeman.Client.Config;
using Lifeman.Client.Net;
using Lifeman.Client.Updates;

namespace Lifeman.Client.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _stagingDir =
        Path.Combine(Path.GetTempPath(), $"lifeman-update-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_stagingDir)) Directory.Delete(_stagingDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Download_VerifiesSha256_StagesArtifact()
    {
        var bytes = Encoding.UTF8.GetBytes("portable-zip pretend payload");
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var (downloader, _) = Build(bytes);

        var staged = await downloader.DownloadAsync(
            new UpdateInfo("1.2.3", sha, "http://kernel.test/dl/lifeman.zip", null),
            CancellationToken.None);

        Assert.NotNull(staged);
        Assert.True(File.Exists(staged!.LocalPath));
        Assert.Equal(bytes.Length, new FileInfo(staged.LocalPath).Length);
    }

    [Fact]
    public async Task Download_RejectsMismatchedSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var wrongSha = new string('0', 64);
        var (downloader, _) = Build(bytes);

        var staged = await downloader.DownloadAsync(
            new UpdateInfo("9.9.9", wrongSha, "http://kernel.test/dl/lifeman.zip", null),
            CancellationToken.None);

        Assert.Null(staged);
        // No half-downloaded files left behind.
        var versionDir = Path.Combine(_stagingDir, "9.9.9");
        if (Directory.Exists(versionDir))
            Assert.Empty(Directory.GetFiles(versionDir));
    }

    [Fact]
    public async Task Download_RefusesManifestWithoutSha()
    {
        var (downloader, _) = Build(Encoding.UTF8.GetBytes("payload"));
        var staged = await downloader.DownloadAsync(
            new UpdateInfo("9.9.9", null, "http://kernel.test/dl/lifeman.zip", null),
            CancellationToken.None);
        Assert.Null(staged);
    }

    [Fact]
    public async Task Download_IsIdempotent_SkipsWhenAlreadyStaged()
    {
        var bytes = Encoding.UTF8.GetBytes("payload");
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var (downloader, handler) = Build(bytes);
        var info = new UpdateInfo("3.3.3", sha, "http://kernel.test/dl/lifeman.zip", null);

        await downloader.DownloadAsync(info, CancellationToken.None);
        var hitsAfterFirst = handler.Hits;
        await downloader.DownloadAsync(info, CancellationToken.None);

        // Second call should not re-fetch — the verified file is on disk.
        Assert.Equal(hitsAfterFirst, handler.Hits);
    }

    private (UpdateDownloader downloader, CountingHandler handler) Build(byte[] body)
    {
        var cfg = new InMemoryConfigStore();
        cfg.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test").AsTask().Wait();
        cfg.SetAsync(ConfigKeys.DeviceToken, "tok").AsTask().Wait();
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        });
        var http = new HttpClient(new DeviceTokenHandler(cfg, handler));
        return (new UpdateDownloader(new LifemanHttpClient(http, cfg), _stagingDir), handler);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public int Hits;
        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Hits);
            return Task.FromResult(_fn(request));
        }
    }
}
