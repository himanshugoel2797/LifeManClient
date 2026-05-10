namespace Lifeman.Client.Config;

/// Persists per-device config. Sensitive fields (the device token) MUST be
/// wrapped by the platform keystore (Android Keystore / Windows DPAPI) inside
/// the platform-specific implementation. The in-memory impl is for tests
/// and never ships in production heads.
public interface IConfigStore
{
    ValueTask<string?> GetAsync(string key, CancellationToken ct = default);
    ValueTask SetAsync(string key, string value, CancellationToken ct = default);
    ValueTask DeleteAsync(string key, CancellationToken ct = default);
}

public static class ConfigKeys
{
    public const string ServerBaseUrl = "server.base_url";
    public const string DeviceToken = "device.token";
    public const string DeviceId = "device.id";
    public const string DeviceName = "device.name";
    public const string SseCursor = "sse.cursor";
    public const string PendingCursor = "pending.cursor";
}
