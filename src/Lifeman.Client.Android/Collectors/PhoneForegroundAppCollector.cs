using System.Text.Json;
using Android.App.Usage;
using Android.Content;
using Android.OS;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.foreground_app` — which app is in the foreground right now,
/// derived from UsageStatsManager.QueryEvents. Polled at 60s, emits
/// only on transitions.
///
/// Requires the user to grant PACKAGE_USAGE_STATS via Settings →
/// Special app access → Usage data access. Without it, this collector
/// silently no-ops — the foreground signal is too cheap to skip when
/// available, but the permission grant is too friction-heavy to make
/// it the default pairing experience.
///
/// 60s cadence is the sweet spot: the OS coalesces usage events into
/// 60s buckets internally, so polling faster mostly returns the same
/// data while still keeping the radio idle.
public sealed class PhoneForegroundAppCollector : ICollector
{
    private readonly Context _ctx;
    private readonly TimeSpan _interval;
    public string Surface => "phone.foreground_app";

    public PhoneForegroundAppCollector(Context ctx, TimeSpan? interval = null)
    {
        _ctx = ctx;
        _interval = interval ?? TimeSpan.FromSeconds(60);
    }

    public static bool HasPermission(Context ctx)
    {
        // Heuristic: ask UsageStatsManager for a window that's
        // guaranteed to have *some* events on any active device. If we
        // get nothing back, the permission almost certainly isn't
        // granted (Android doesn't expose a direct API for this).
        var usm = (UsageStatsManager?)ctx.GetSystemService(Context.UsageStatsService);
        if (usm is null) return false;
        var end = Java.Lang.JavaSystem.CurrentTimeMillis();
        var start = end - TimeSpan.FromHours(1).Ticks / TimeSpan.TicksPerMillisecond;
        try
        {
            var events = usm.QueryEvents(start, end);
            return events?.HasNextEvent ?? false;
        }
        catch { return false; }
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var usm = (UsageStatsManager?)_ctx.GetSystemService(Context.UsageStatsService);
        if (usm is null) yield break;
        if (!HasPermission(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.foreground_app: PACKAGE_USAGE_STATS not granted, collector idle");
            yield break;
        }

        string? lastPackage = null;
        var cursor = Java.Lang.JavaSystem.CurrentTimeMillis() - 1000;

        while (!ct.IsCancellationRequested)
        {
            var now = Java.Lang.JavaSystem.CurrentTimeMillis();
            UsageEvents? events = null;
            try { events = usm.QueryEvents(cursor, now); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("lifeman",
                    $"phone.foreground_app: QueryEvents failed: {ex.Message}");
            }
            cursor = now;

            if (events is not null)
            {
                var ev = new UsageEvents.Event();
                while (events.HasNextEvent && events.GetNextEvent(ev))
                {
                    // MOVE_TO_FOREGROUND = 1
                    if (ev.EventType != UsageEventType.MoveToForeground) continue;
                    var pkg = ev.PackageName;
                    if (string.IsNullOrEmpty(pkg) || pkg == lastPackage) continue;
                    lastPackage = pkg;
                    var payload = JsonSerializer.Serialize(new
                    {
                        package = pkg,
                        class_name = ev.ClassName,
                        event_time_ms = ev.TimeStamp,
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    });
                    yield return new CollectedEvent(Surface, payload,
                        DateTimeOffset.FromUnixTimeMilliseconds(ev.TimeStamp));
                }
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (System.OperationCanceledException) { yield break; }
        }
    }
}
