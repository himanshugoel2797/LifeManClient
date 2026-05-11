using System.Text.Json;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.Content;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.bt_audio` — Bluetooth audio connect / disconnect transitions
/// via the A2DP and Headset profile broadcasts. Pure event-driven, no
/// polling, but requires BLUETOOTH_CONNECT runtime permission on
/// Android 12+ (S, API 31). On older releases the manifest BLUETOOTH
/// permission alone suffices, no runtime grant needed.
///
/// Self-disables if the runtime grant is missing on S+. The kernel
/// uses this to recognise "user just plugged in headphones, likely
/// switching to focused work" and similar transitions.
public sealed class PhoneBluetoothAudioCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.bt_audio";

    public PhoneBluetoothAudioCollector(Context ctx) => _ctx = ctx;

    public static bool HasPermission(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.S) return true;
        return ContextCompat.CheckSelfPermission(ctx, global::Android.Manifest.Permission.BluetoothConnect)
            == Permission.Granted;
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!HasPermission(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.bt_audio: BLUETOOTH_CONNECT not granted, collector idle");
            yield return ClientObservations.CollectorDisabled(Surface, "BLUETOOTH_CONNECT not granted");
            yield break;
        }

        await foreach (var ev in ChannelCollectorScaffold.StreamAsync(Attach, ct).ConfigureAwait(false))
            yield return ev;
    }

    private IDisposable Attach(Action<CollectedEvent> emit)
    {
        var receiver = new ActionBroadcastReceiver(intent =>
        {
            var profile = intent.Action switch
            {
                _ when intent.Action == BluetoothA2dp.ActionConnectionStateChanged => "a2dp",
                _ when intent.Action == BluetoothHeadset.ActionConnectionStateChanged => "headset",
                _ => "unknown",
            };
            if (profile == "unknown") return;
            var state = intent.GetIntExtra(BluetoothProfile.ExtraState, -1);
            var device = (BluetoothDevice?)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
            emit(BuildEvent(profile, state, device));
        });

        var filter = new IntentFilter();
        filter.AddAction(BluetoothA2dp.ActionConnectionStateChanged);
        filter.AddAction(BluetoothHeadset.ActionConnectionStateChanged);
        _ctx.RegisterReceiver(receiver, filter);

        return ChannelCollectorScaffold.Teardown(
            () => { try { _ctx.UnregisterReceiver(receiver); } catch { } });
    }

    private CollectedEvent BuildEvent(string profile, int state, BluetoothDevice? device)
    {
        var payload = JsonSerializer.Serialize(new
        {
            profile,
            state = state switch
            {
                (int)ProfileState.Connected => "connected",
                (int)ProfileState.Disconnected => "disconnected",
                (int)ProfileState.Connecting => "connecting",
                (int)ProfileState.Disconnecting => "disconnecting",
                _ => "unknown",
            },
            device_address = device?.Address,
            device_name = device?.Name,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }
}
