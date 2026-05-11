using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Lifeman.Client.Collectors;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.notification` — subscribes to incoming user notifications via
/// the UWP `UserNotificationListener` and emits one event per
/// notification with `{app_id, app_display_name, title, body, timestamp}`.
///
/// Requires runtime access via `RequestAccessAsync()`. If the user denies
/// or the API is unavailable on this Windows edition, the collector emits
/// a single `client.observation` self-disable event and yield-breaks —
/// same convention as other gated collectors.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DesktopNotificationCollector : ICollector
{
    public string Surface => "desktop.notification";

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (listener, disabledReason) = await TryInitListenerAsync().ConfigureAwait(false);
        if (listener is null)
        {
            yield return ClientObservations.CollectorDisabled(Surface,
                disabledReason ?? "init failed");
            yield break;
        }

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var seen = new HashSet<uint>();

        // Seed: backfill anything already in the listener so first-run
        // doesn't lose context.
        await BackfillAsync(listener, seen, channel.Writer).ConfigureAwait(false);

        TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs> handler =
            (sender, args) =>
            {
                if (args.ChangeKind != UserNotificationChangedKind.Added) return;
                try
                {
                    var notif = sender.GetNotification(args.UserNotificationId);
                    if (notif is null) return;
                    lock (seen) { if (!seen.Add(notif.Id)) return; }
                    var ev = TryBuildEvent(notif);
                    if (ev is not null) channel.Writer.TryWrite(ev);
                }
                catch
                {
                    // Skip individual decode failures rather than tearing down the stream.
                }
            };

        var subscribed = false;
        string? subscribeError = null;
        try
        {
            listener.NotificationChanged += handler;
            subscribed = true;
        }
        catch (Exception ex)
        {
            subscribeError = ex.Message;
        }

        if (!subscribed)
        {
            yield return ClientObservations.CollectorDisabled(Surface,
                $"NotificationChanged subscribe failed: {subscribeError}");
            yield break;
        }

        using var reg = ct.Register(() =>
        {
            try { listener.NotificationChanged -= handler; } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    private static async Task<(UserNotificationListener? Listener, string? Error)> TryInitListenerAsync()
    {
        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
        }
        catch (Exception ex)
        {
            return (null, $"UserNotificationListener unavailable: {ex.Message}");
        }

        UserNotificationListenerAccessStatus access;
        try
        {
            access = await listener.RequestAccessAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"RequestAccessAsync threw: {ex.Message}");
        }

        if (access != UserNotificationListenerAccessStatus.Allowed)
            return (null, $"access not granted: {access}");

        return (listener, null);
    }

    private static async Task BackfillAsync(
        UserNotificationListener listener, HashSet<uint> seen, ChannelWriter<CollectedEvent> writer)
    {
        try
        {
            var existing = await listener.GetNotificationsAsync(NotificationKinds.Toast)
                .AsTask().ConfigureAwait(false);
            foreach (var n in existing)
            {
                bool isNew;
                lock (seen) { isNew = seen.Add(n.Id); }
                if (!isNew) continue;
                var ev = TryBuildEvent(n);
                if (ev is not null) writer.TryWrite(ev);
            }
        }
        catch
        {
            // Backfill is best-effort; live subscription is what matters.
        }
    }

    private static CollectedEvent? TryBuildEvent(UserNotification n)
    {
        string? title = null;
        string? body = null;
        try
        {
            var toast = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (toast is not null && toast.GetTextElements() is { } texts)
            {
                var list = texts.ToList();
                if (list.Count > 0) title = list[0]?.Text;
                if (list.Count > 1)
                    body = string.Join("\n", list.Skip(1).Select(t => t.Text).Where(s => !string.IsNullOrEmpty(s)));
            }
        }
        catch { }

        string? appId = null;
        string? appDisplay = null;
        try
        {
            appId = n.AppInfo?.AppUserModelId;
            appDisplay = n.AppInfo?.DisplayInfo?.DisplayName;
        }
        catch { }

        // Drop entries that have nothing useful — an empty toast is just
        // padding for the LLM.
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body) && string.IsNullOrEmpty(appId))
            return null;

        var payload = JsonSerializer.Serialize(new
        {
            app_id = appId,
            app_display_name = appDisplay,
            title,
            body,
            timestamp = (n.CreationTime == default ? DateTimeOffset.UtcNow : n.CreationTime.ToUniversalTime())
                .ToString("O"),
        });
        return new CollectedEvent("desktop.notification", payload, DateTimeOffset.UtcNow);
    }
}
