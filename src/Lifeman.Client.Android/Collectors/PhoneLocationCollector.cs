using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.Content;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.location` — coarse location, biased aggressively toward
/// battery. Two ingest paths:
///
///   1. PASSIVE_PROVIDER: free-rides on location updates that other
///      apps request. We do not power any radios on our own.
///   2. Periodic "last known" snapshot every 10 minutes across all
///      providers, so we still report a position even if nothing else
///      on the phone has asked for one recently.
///
/// No active GPS / network locating from us. The kernel does not need
/// real-time precision; routine cadence is plenty. Requires
/// ACCESS_FINE_LOCATION; ACCESS_BACKGROUND_LOCATION lets us keep
/// receiving updates when the foreground service is running.
public sealed class PhoneLocationCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.location";

    public PhoneLocationCollector(Context ctx) => _ctx = ctx;

    public static bool HasPermission(Context ctx) =>
        ContextCompat.CheckSelfPermission(ctx, global::Android.Manifest.Permission.AccessFineLocation)
            == Permission.Granted
        || ContextCompat.CheckSelfPermission(ctx, global::Android.Manifest.Permission.AccessCoarseLocation)
            == Permission.Granted;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!HasPermission(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.location: location permission not granted, collector idle");
            yield return ClientObservations.CollectorDisabled(Surface, "ACCESS_FINE/COARSE_LOCATION not granted");
            yield break;
        }

        var lm = (LocationManager?)_ctx.GetSystemService(Context.LocationService);
        if (lm is null)
        {
            yield return ClientObservations.CollectorDisabled(Surface, "LocationManager unavailable");
            yield break;
        }

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void PushLocation(string trigger, Location? loc)
        {
            if (loc is null) return;
            var payload = JsonSerializer.Serialize(new
            {
                trigger,
                provider = loc.Provider,
                lat = loc.Latitude,
                lon = loc.Longitude,
                accuracy_m = loc.HasAccuracy ? loc.Accuracy : (float?)null,
                altitude_m = loc.HasAltitude ? loc.Altitude : (double?)null,
                speed_mps = loc.HasSpeed ? loc.Speed : (float?)null,
                bearing_deg = loc.HasBearing ? loc.Bearing : (float?)null,
                fix_time_ms = loc.Time,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            channel.Writer.TryWrite(new CollectedEvent("phone.location", payload, DateTimeOffset.UtcNow));
        }

        // Dedicated HandlerThread so location callbacks don't run on
        // the UI thread — Maps and similar apps can be chatty enough
        // that even our 5-min throttle still bursts a few callbacks
        // back-to-back, and we'd rather not stall input dispatch.
        var handlerThread = new HandlerThread("lifeman-location");
        handlerThread.Start();

        var listener = new PassiveListener(loc => PushLocation("passive_update", loc));
        try
        {
            // Minimum time 5min, minimum distance 50m — even on passive
            // we throttle so a chatty foreground app doesn't bury us.
            lm.RequestLocationUpdates(
                LocationManager.PassiveProvider!,
                (long)TimeSpan.FromMinutes(5).TotalMilliseconds,
                50f,
                listener,
                handlerThread.Looper!);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("lifeman",
                $"phone.location: passive request failed: {ex.Message}");
        }

        // Startup + periodic "last known" snapshots — independent of
        // whether anything else on the phone is asking for location.
        Location? Best()
        {
            Location? best = null;
            foreach (var p in lm.GetProviders(true) ?? new List<string>())
            {
                try
                {
                    var l = lm.GetLastKnownLocation(p);
                    if (l is null) continue;
                    if (best is null || l.Time > best.Time) best = l;
                }
                catch { /* provider may be disabled */ }
            }
            return best;
        }

        PushLocation("startup", Best());
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                    PushLocation("periodic", Best());
                }
            }
            catch (System.OperationCanceledException) { }
        });

        using var reg = ct.Register(() =>
        {
            try { lm.RemoveUpdates(listener); } catch { }
            try { handlerThread.QuitSafely(); } catch { }
            // Bound the join so a wedged native callback can't block
            // shutdown indefinitely. 1s is plenty for the looper to drain.
            try { handlerThread.Join(1000); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private sealed class PassiveListener : Java.Lang.Object, ILocationListener
    {
        private readonly Action<Location> _onLocation;
        public PassiveListener(Action<Location> onLocation) => _onLocation = onLocation;
        public void OnLocationChanged(Location location) => _onLocation(location);
        public void OnProviderDisabled(string provider) { }
        public void OnProviderEnabled(string provider) { }
        public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }
    }
}
