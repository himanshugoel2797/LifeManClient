using Lifeman.Client.Windows.Logging;
using Microsoft.Extensions.Logging;

namespace Lifeman.Client.Windows.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"lifeman-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Writes_Lines_To_Daily_File()
    {
        using (var p = new RollingFileLoggerProvider(_dir))
        {
            var log = p.CreateLogger("test");
            log.Log(LogLevel.Information, default, "hello world 42", null, (s, _) => s);
            log.Log(LogLevel.Warning, default, "warned", null, (s, _) => s);
            await Task.Delay(200);
        }

        var files = Directory.GetFiles(_dir, "client-*.log");
        Assert.Single(files);
        var content = await File.ReadAllTextAsync(files[0]);
        Assert.True(content.Contains("hello world 42"), $"missing info; got:\n{content}");
        Assert.True(content.Contains("WRN"), $"missing warning; got:\n{content}");
        Assert.True(content.Contains("test:"), $"missing category; got:\n{content}");
    }
}
