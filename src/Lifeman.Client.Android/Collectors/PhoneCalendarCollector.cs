using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Provider;
using AndroidX.Core.Content;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.calendar` — upcoming events from CalendarContract within a
/// rolling 24h window. Driven by a ContentObserver on
/// CalendarContract.Events; a fallback periodic refresh every 15
/// minutes catches edge cases where the observer doesn't fire (e.g.
/// the OS hasn't noticed a sync yet but the time-window slid past
/// some events).
///
/// Requires READ_CALENDAR (runtime). Self-disables if missing.
public sealed class PhoneCalendarCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.calendar";

    public PhoneCalendarCollector(Context ctx) => _ctx = ctx;

    public static bool HasPermission(Context ctx) =>
        ContextCompat.CheckSelfPermission(ctx, global::Android.Manifest.Permission.ReadCalendar)
            == Permission.Granted;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!HasPermission(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.calendar: READ_CALENDAR not granted, collector idle");
            yield return ClientObservations.CollectorDisabled(Surface, "READ_CALENDAR not granted");
            yield break;
        }

        var resolver = _ctx.ContentResolver;
        if (resolver is null)
        {
            yield return ClientObservations.CollectorDisabled(Surface, "ContentResolver unavailable");
            yield break;
        }

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void Push(string trigger)
        {
            try { channel.Writer.TryWrite(Snapshot(trigger, resolver)); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("lifeman", $"phone.calendar snapshot failed: {ex.Message}");
            }
        }

        var observer = new CalendarObserver(() => Push("calendar_changed"));
        resolver.RegisterContentObserver(CalendarContract.Events.ContentUri!, true, observer);

        Push("startup");

        using var reg = ct.Register(() =>
        {
            try { resolver.UnregisterContentObserver(observer); } catch { }
            channel.Writer.TryComplete();
        });

        // 15-min fallback poll runs in parallel with the observer.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
                    Push("periodic");
                }
            }
            catch (System.OperationCanceledException) { }
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private static CollectedEvent Snapshot(string trigger, ContentResolver resolver)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var horizon = now + (long)TimeSpan.FromHours(24).TotalMilliseconds;

        // CalendarContract.Instances expands recurring events into
        // concrete instances inside the begin/end window — exactly what
        // we want for "what's coming up." Querying Events instead would
        // miss the next occurrence of a recurring entry.
        var builder = CalendarContract.Instances.ContentUri!.BuildUpon()!;
        builder.AppendPath(now.ToString());
        builder.AppendPath(horizon.ToString());
        var uri = builder.Build();

        // Column names are stable Android contract constants; the C#
        // binding scatters them across InterfaceConsts members that
        // don't all surface for Instances, so use the canonical string
        // names directly.
        var projection = new[] { "event_id", "title", "begin", "end", "allDay", "eventLocation", "calendar_displayName" };

        var items = new List<object>();
        using var cursor = resolver.Query(uri!, projection, null, null, "begin ASC");
        if (cursor is not null)
        {
            while (cursor.MoveToNext() && items.Count < 50)
            {
                items.Add(new
                {
                    event_id = cursor.GetLong(0),
                    title = cursor.IsNull(1) ? null : cursor.GetString(1),
                    begin_ms = cursor.GetLong(2),
                    end_ms = cursor.GetLong(3),
                    all_day = cursor.GetInt(4) == 1,
                    location = cursor.IsNull(5) ? null : cursor.GetString(5),
                    calendar = cursor.IsNull(6) ? null : cursor.GetString(6),
                });
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            window_start_ms = now,
            window_end_ms = horizon,
            count = items.Count,
            events = items,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("phone.calendar", payload, DateTimeOffset.UtcNow);
    }

    private sealed class CalendarObserver : ContentObserver
    {
        private readonly Action _onChange;
        public CalendarObserver(Action onChange) : base(null) => _onChange = onChange;
        public override void OnChange(bool selfChange) => _onChange();
    }
}
