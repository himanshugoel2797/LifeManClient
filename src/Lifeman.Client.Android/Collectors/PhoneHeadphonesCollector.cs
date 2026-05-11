using System.Text.Json;
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

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            var receiver = new ActionBroadcastReceiver(intent =>
            {
                if (intent.Action == AudioManager.ActionHeadsetPlug)
                    emit(Build("headset_plug", intent));
            });

            // Sticky broadcast: the returned intent represents the current state.
            var sticky = _ctx.RegisterReceiver(receiver, new IntentFilter(AudioManager.ActionHeadsetPlug));
            if (sticky is not null) emit(Build("startup", sticky));

            return ChannelCollectorScaffold.Teardown(
                () => { try { _ctx.UnregisterReceiver(receiver); } catch { } });
        }, ct);

    private CollectedEvent Build(string trigger, Intent intent)
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
}
