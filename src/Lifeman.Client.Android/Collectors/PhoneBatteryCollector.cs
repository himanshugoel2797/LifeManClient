using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.OS;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.battery` — emits on ACTION_BATTERY_CHANGED + power-connect /
/// disconnect broadcasts. ACTION_BATTERY_CHANGED is sticky, so the
/// initial registerReceiver returns the latest state without us having
/// to poll — that doubles as our startup snapshot.
public sealed class PhoneBatteryCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.battery";

    public PhoneBatteryCollector(Context ctx) => _ctx = ctx;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var receiver = new BatteryReceiver((intent, trigger) =>
        {
            var ev = BuildEvent(intent, trigger);
            if (ev is not null) channel.Writer.TryWrite(ev);
        });

        var filter = new IntentFilter();
        filter.AddAction(Intent.ActionBatteryChanged);
        filter.AddAction(Intent.ActionPowerConnected);
        filter.AddAction(Intent.ActionPowerDisconnected);

        // Sticky broadcast: registerReceiver returns the current
        // battery intent immediately, which becomes our snapshot.
        var sticky = _ctx.RegisterReceiver(receiver, filter);
        if (sticky is not null)
        {
            var ev = BuildEvent(sticky, "startup");
            if (ev is not null) channel.Writer.TryWrite(ev);
        }

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private CollectedEvent? BuildEvent(Intent intent, string trigger)
    {
        int level = intent.GetIntExtra(BatteryManager.ExtraLevel, -1);
        int scale = intent.GetIntExtra(BatteryManager.ExtraScale, -1);
        int status = intent.GetIntExtra(BatteryManager.ExtraStatus, -1);
        int plugged = intent.GetIntExtra(BatteryManager.ExtraPlugged, -1);
        int temperature = intent.GetIntExtra(BatteryManager.ExtraTemperature, -1);
        var charging = status == (int)BatteryStatus.Charging || status == (int)BatteryStatus.Full;
        var pct = (level >= 0 && scale > 0) ? (float?)level / scale : null;

        var plug = plugged switch
        {
            (int)BatteryPlugged.Ac => "ac",
            (int)BatteryPlugged.Usb => "usb",
            (int)BatteryPlugged.Wireless => "wireless",
            0 => "unplugged",
            _ => "unknown",
        };

        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            battery_level = pct,
            battery_charging = charging,
            plug,
            temperature_c = temperature > 0 ? temperature / 10.0 : (double?)null,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }

    private sealed class BatteryReceiver : BroadcastReceiver
    {
        private readonly Action<Intent, string> _onIntent;
        public BatteryReceiver(Action<Intent, string> onIntent) => _onIntent = onIntent;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent is null) return;
            var trigger = intent.Action switch
            {
                Intent.ActionPowerConnected => "power_connected",
                Intent.ActionPowerDisconnected => "power_disconnected",
                Intent.ActionBatteryChanged => "battery_changed",
                _ => "unknown",
            };
            _onIntent(intent, trigger);
        }
    }
}
