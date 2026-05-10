using Lifeman.Client.Config;
using Lifeman.Client.Windows.Config;

namespace Lifeman.Client.Windows.Tests;

public sealed class DpapiConfigStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"lifeman-dpapi-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task RoundTrips_Sensitive_And_Plain_Values()
    {
        var s = new DpapiConfigStore(_path);
        await s.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        await s.SetAsync(ConfigKeys.DeviceId, "dev123");
        await s.SetAsync(ConfigKeys.DeviceToken, "super-secret-token");

        // Reload in a fresh store to prove it reads from disk + decrypts.
        var fresh = new DpapiConfigStore(_path);
        Assert.Equal("http://kernel.test", await fresh.GetAsync(ConfigKeys.ServerBaseUrl));
        Assert.Equal("dev123", await fresh.GetAsync(ConfigKeys.DeviceId));
        Assert.Equal("super-secret-token", await fresh.GetAsync(ConfigKeys.DeviceToken));
    }

    [Fact]
    public async Task Token_Is_Encrypted_On_Disk()
    {
        var s = new DpapiConfigStore(_path);
        await s.SetAsync(ConfigKeys.DeviceToken, "PLAINTEXT-MARKER-12345");

        var onDisk = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain("PLAINTEXT-MARKER-12345", onDisk);
        // The token entry is the object-shaped value with a `p` key, not a raw string.
        Assert.Contains("\"p\":", onDisk);
    }

    [Fact]
    public async Task Plain_Values_Are_Not_Wrapped()
    {
        var s = new DpapiConfigStore(_path);
        await s.SetAsync(ConfigKeys.ServerBaseUrl, "http://kernel.test");
        var onDisk = await File.ReadAllTextAsync(_path);
        Assert.Contains("\"server.base_url\": \"http://kernel.test\"", onDisk);
    }

    [Fact]
    public async Task Delete_Removes_Value()
    {
        var s = new DpapiConfigStore(_path);
        await s.SetAsync(ConfigKeys.DeviceToken, "x");
        await s.DeleteAsync(ConfigKeys.DeviceToken);
        var fresh = new DpapiConfigStore(_path);
        Assert.Null(await fresh.GetAsync(ConfigKeys.DeviceToken));
    }
}
