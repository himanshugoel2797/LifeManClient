using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.OS;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.idle` — Doze and light-idle transitions. The system tells us
/// when the device enters/exits Doze (full and light) via
/// PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED /
/// ACTION_LIGHT_DEVICE_IDLE_MODE_CHANGED — both protected broadcasts
/// the OS sends, so we don't poll. Doze is the dominant battery-aware
/// state on Android; the kernel can use it to back off chatty surfaces.
public sealed class PhoneIdleCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.idle";

    public PhoneIdleCollector(Context ctx) => _ctx = ctx;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var pm = (PowerManager?)_ctx.GetSystemService(Context.PowerService);
        if (pm is null) yield break;

        void Push(string trigger)
        {
            // Light-idle (ACTION_LIGHT_DEVICE_IDLE_MODE_CHANGED) is a
            // SystemApi — not exposed in the public Mono.Android
            // binding — so we report only deep Doze + power-save here.
            var payload = JsonSerializer.Serialize(new
            {
                trigger,
                deep_idle = pm.IsDeviceIdleMode,
                power_save = pm.IsPowerSaveMode,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            channel.Writer.TryWrite(new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow));
        }

        var receiver = new IdleReceiver(Push);
        var filter = new IntentFilter();
        filter.AddAction(PowerManager.ActionDeviceIdleModeChanged);
        filter.AddAction(PowerManager.ActionPowerSaveModeChanged);
        _ctx.RegisterReceiver(receiver, filter);

        Push("startup");

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private sealed class IdleReceiver : BroadcastReceiver
    {
        private readonly Action<string> _onChange;
        public IdleReceiver(Action<string> onChange) => _onChange = onChange;

        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case PowerManager.ActionDeviceIdleModeChanged: _onChange("deep_idle_changed"); break;
                case PowerManager.ActionPowerSaveModeChanged: _onChange("power_save_changed"); break;
            }
        }
    }
}
