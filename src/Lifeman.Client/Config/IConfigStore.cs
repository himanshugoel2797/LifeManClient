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
    public const string PendingCursor = "pending.cursor";

    /// Set to "1" by Uploader/SseReceiver when the kernel returns 401
    /// (token revoked or evicted). Cleared by PairingClient on a
    /// successful re-pair. Heads observe this to switch into a
    /// re-pair UI rather than churning silently against a dead token.
    public const string RepairRequired = "auth.repair_required";

    /// Comma-separated list of Android package names whose notifications
    /// should be enriched with title / text / subText / ticker on
    /// upload. Empty (default) → metadata-only for every package.
    /// Match is "exact OR prefix-match", so `com.google.` covers all
    /// Google apps without listing them individually.
    public const string NotificationRichPackages = "phone.notification.rich_packages";
}
