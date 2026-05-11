using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Devices;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.audio_endpoint` — emits when the default audio render
/// device changes (headphones plugged in, USB DAC connected, default
/// device selected in the volume mixer). Mirror of `phone.headphones`
/// + `phone.bt_audio` on Android.
///
/// Uses `Windows.Media.Devices.MediaDevice.DefaultAudioRenderDeviceChanged`
/// for the trigger and `DeviceInformation.CreateFromIdAsync` to resolve
/// the device's friendly name and form factor.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DesktopAudioEndpointCollector : ICollector
{
    public string Surface => "desktop.audio_endpoint";

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            void Push(string trigger)
            {
                try { emit(Snapshot(trigger)); }
                catch (Exception ex)
                {
                    emit(new CollectedEvent("desktop.audio_endpoint",
                        JsonSerializer.Serialize(new { trigger, error = ex.Message }),
                        DateTimeOffset.UtcNow));
                }
            }

            Push("startup");

            TypedEventHandler<object, DefaultAudioRenderDeviceChangedEventArgs> handler =
                (_, e) => Push($"render_changed_{e.Role.ToString().ToLowerInvariant()}");
            MediaDevice.DefaultAudioRenderDeviceChanged += handler;

            return ChannelCollectorScaffold.Teardown(() =>
                MediaDevice.DefaultAudioRenderDeviceChanged -= handler);
        }, ct);

    private static CollectedEvent Snapshot(string trigger)
    {
        string? id = null, name = null, formFactor = null;
        try
        {
            id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (!string.IsNullOrEmpty(id))
            {
                var info = DeviceInformation.CreateFromIdAsync(id).AsTask().GetAwaiter().GetResult();
                name = info?.Name;
                if (info?.Properties.TryGetValue("System.Devices.InterfaceClassGuid", out var ifc) == true)
                    formFactor = ifc?.ToString();
            }
        }
        catch { /* swallow — emit best-effort */ }

        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            endpoint_id = id,
            endpoint_name = name,
            form_factor = formFactor,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.audio_endpoint", payload, DateTimeOffset.UtcNow);
    }
}
