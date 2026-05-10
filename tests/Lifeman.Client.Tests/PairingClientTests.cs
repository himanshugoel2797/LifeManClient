using Lifeman.Client.Net;

namespace Lifeman.Client.Tests;

public sealed class PairingClientTests
{
    [Fact]
    public void ParsePairUrl_ExtractsHostAndCode()
    {
        var (host, code) = PairingClient.ParsePairUrl("lifeman://pair?host=http%3A%2F%2F10.0.0.5%3A8390&code=ABCDEFGH");
        Assert.Equal("http://10.0.0.5:8390", host);
        Assert.Equal("ABCDEFGH", code);
    }

    [Fact]
    public void ParsePairUrl_RejectsWrongScheme()
    {
        Assert.Throws<ArgumentException>(() => PairingClient.ParsePairUrl("https://pair?host=x&code=y"));
    }

    [Fact]
    public void ParsePairUrl_RequiresBothParams()
    {
        Assert.Throws<ArgumentException>(() => PairingClient.ParsePairUrl("lifeman://pair?host=http://x"));
    }
}
