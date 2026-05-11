using System.Text.Json;
using Android.Content;
using Lifeman.Client.Android.Services;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.notification` — drains the static event channel that
/// LifemanNotificationListener writes to. Self-disables if the user
/// hasn't granted Notification access (the listener service never
/// gets bound by the OS, so no events ever arrive — but checking up
/// front keeps the log honest).
public sealed class PhoneNotificationCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.notification";

    public PhoneNotificationCollector(Context ctx) => _ctx = ctx;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!LifemanNotificationListener.IsEnabled(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.notification: notification access not granted, collector idle");
            yield break;
        }

        await foreach (var ev in LifemanNotificationListener.Events.Reader
            .ReadAllAsync(ct).ConfigureAwait(false))
        {
            var payload = JsonSerializer.Serialize(new
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
            });
            yield return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
        }
    }
}
