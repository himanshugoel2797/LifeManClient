using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Media;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.headphones` — wired headset plug/unplug via ACTION_HEADSET_PLUG
/// (sticky broadcast, no permission). Wireless / Bluetooth audio is a
/// separate surface — it needs BLUETOOTH_CONNECT and AudioDeviceCallback,
/// and is deliberately deferred until the kernel actually wants it.
public sealed class PhoneHeadphonesCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.headphones";

    public PhoneHeadphonesCollector(Context ctx) => _ctx = ctx;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var receiver = new HeadsetReceiver((trigger, intent) =>
            channel.Writer.TryWrite(Emit(trigger, intent)));

        var filter = new IntentFilter(AudioManager.ActionHeadsetPlug);
        // Sticky: the returned intent represents the current state.
        var sticky = _ctx.RegisterReceiver(receiver, filter);
        if (sticky is not null) channel.Writer.TryWrite(Emit("startup", sticky));

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private CollectedEvent Emit(string trigger, Intent intent)
    {
        var state = intent.GetIntExtra("state", -1); // 0 unplugged, 1 plugged
        var hasMic = intent.GetIntExtra("microphone", -1) == 1;
        var name = intent.GetStringExtra("name");
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            plugged = state == 1,
            has_microphone = hasMic,
            name,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }

    private sealed class HeadsetReceiver : BroadcastReceiver
    {
        private readonly Action<string, Intent> _onChange;
        public HeadsetReceiver(Action<string, Intent> onChange) => _onChange = onChange;
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionHeadsetPlug)
                _onChange("headset_plug", intent);
        }
    }
}
