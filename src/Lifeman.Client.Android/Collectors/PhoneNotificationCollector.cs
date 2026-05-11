using System.Text.Json;
using Android.Content;
using Lifeman.Client.Android.Services;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;

namespace Lifeman.Client.Android.Collectors;

/// `phone.notification` — drains the static event channel that
/// LifemanNotificationListener writes to. Self-disables if the user
/// hasn't granted Notification access.
///
/// Per-package enrichment: by default we emit metadata only (package,
/// ids, channel, flags) because notification content is regularly
/// sensitive (texts, banking, 2FA codes). Packages listed in the
/// `phone.notification.rich_packages` config key (exact match OR
/// prefix-match) get title / text / subText / ticker forwarded too.
/// The list is read once at collector startup; restart the agent to
/// pick up changes.
public sealed class PhoneNotificationCollector : ICollector
{
    private readonly Context _ctx;
    private readonly IConfigStore _config;
    public string Surface => "phone.notification";

    public PhoneNotificationCollector(Context ctx, IConfigStore config)
    {
        _ctx = ctx;
        _config = config;
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!LifemanNotificationListener.IsEnabled(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.notification: notification access not granted, collector idle");
            yield break;
        }

        var richList = await LoadRichListAsync(ct).ConfigureAwait(false);
        if (richList.Count > 0)
            global::Android.Util.Log.Info("lifeman",
                $"phone.notification: rich payload enabled for {richList.Count} package(s)");

        await foreach (var ev in LifemanNotificationListener.Events.Reader
            .ReadAllAsync(ct).ConfigureAwait(false))
        {
            var rich = IsRich(ev.Package, richList);
            var payload = rich
                ? (object)new
                {
                    trigger = ev.Posted ? "posted" : "removed",
                    package = ev.Package,
                    tag = ev.Tag,
                    id = ev.Id,
                    category = ev.Category,
                    channel_id = ev.ChannelId,
                    post_time_ms = ev.PostTimeMs,
                    ongoing = ev.Ongoing,
                    clearable = ev.Clearable,
                    title = ev.Title,
                    text = ev.Text,
                    sub_text = ev.SubText,
                    ticker = ev.Ticker,
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                }
                : new
                {
                    trigger = ev.Posted ? "posted" : "removed",
                    package = ev.Package,
                    tag = ev.Tag,
                    id = ev.Id,
                    category = ev.Category,
                    channel_id = ev.ChannelId,
                    post_time_ms = ev.PostTimeMs,
                    ongoing = ev.Ongoing,
                    clearable = ev.Clearable,
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                };
            yield return new CollectedEvent(Surface,
                JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow);
        }
    }

    private async Task<List<string>> LoadRichListAsync(CancellationToken ct)
    {
        var raw = await _config.GetAsync(ConfigKeys.NotificationRichPackages, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool IsRich(string package, List<string> richList)
    {
        foreach (var p in richList)
        {
            if (package == p) return true;
            if (p.EndsWith('.') && package.StartsWith(p, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
