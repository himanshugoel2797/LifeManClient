using System.Threading.Channels;
using Android.App;
using Android.Content;
using Android.Service.Notification;
using AndroidX.Core.App;

namespace Lifeman.Client.Android.Services;

/// NotificationListenerService that the OS binds to once the user
/// grants "Notification access" in Settings. Receives every posted /
/// removed notification system-wide and exposes them via a static
/// channel that PhoneNotificationCollector drains.
///
/// Existence of this enabled service is also the key that unlocks
/// MediaSessionManager.GetActiveSessions(componentName) — same
/// permission unlocks both surfaces.
[Service(
    Enabled = true,
    Exported = true,
    Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE")]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public sealed class LifemanNotificationListener : NotificationListenerService
{
    public static Channel<NotificationEvent> Events { get; } =
        Channel.CreateUnbounded<NotificationEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

    public static bool IsEnabled(Context ctx)
    {
        var pkg = ctx.PackageName;
        return pkg is not null
            && NotificationManagerCompat.GetEnabledListenerPackages(ctx).Contains(pkg);
    }

    public static ComponentName ComponentName(Context ctx) =>
        new(ctx, Java.Lang.Class.FromType(typeof(LifemanNotificationListener)));

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn is null) return;
        Events.Writer.TryWrite(NotificationEvent.From(sbn, posted: true));
    }

    public override void OnNotificationRemoved(StatusBarNotification? sbn)
    {
        if (sbn is null) return;
        Events.Writer.TryWrite(NotificationEvent.From(sbn, posted: false));
    }
}

/// Minimal projection of StatusBarNotification — package, ids, category,
/// post time, and the broad-strokes flags. We deliberately do NOT carry
/// title / text / extras: those contain PII (texts, emails, banking app
/// content) that the kernel can opt into per-app later if it really
/// wants. First-cut payload is "which app made noise, when, and was
/// it ongoing / clearable" — enough for routine recognition.
/// Richer than the kernel will see by default: title / text / subText /
/// ticker live on the listener-side event so PhoneNotificationCollector
/// can choose, per-package, whether to forward them. Default policy is
/// metadata-only.
public sealed record NotificationEvent(
    bool Posted,
    string Package,
    string? Tag,
    int Id,
    string? Category,
    string? ChannelId,
    long PostTimeMs,
    bool Ongoing,
    bool Clearable,
    string? Title,
    string? Text,
    string? SubText,
    string? Ticker)
{
    public static NotificationEvent From(StatusBarNotification sbn, bool posted)
    {
        var n = sbn.Notification;
        var extras = n?.Extras;
        return new NotificationEvent(
            Posted: posted,
            Package: sbn.PackageName ?? "?",
            Tag: sbn.Tag,
            Id: sbn.Id,
            Category: n?.Category,
            ChannelId: n?.ChannelId,
            PostTimeMs: sbn.PostTime,
            Ongoing: sbn.IsOngoing,
            Clearable: sbn.IsClearable,
            Title: extras?.GetCharSequence("android.title")?.ToString(),
            Text: extras?.GetCharSequence("android.text")?.ToString()
                  ?? extras?.GetCharSequence("android.bigText")?.ToString(),
            SubText: extras?.GetCharSequence("android.subText")?.ToString(),
            Ticker: n?.TickerText?.ToString());
    }
}
