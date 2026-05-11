using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Net;
using Lifeman.Client.Collectors;
using Lifeman.Client.Net;

namespace Lifeman.Client.Android.Collectors;

/// `phone.network` — connectivity transitions via
/// ConnectivityManager.NetworkCallback. Event-driven, no polling.
/// Also retunes the uploader's batch profile when metering changes
/// (small batches on Wi-Fi for freshness, large batches on cellular
/// to amortise radio wakes).
///
/// Capabilities tracked: transport (wifi/cellular/ethernet/vpn),
/// validated (real internet), metered, not_roaming, captive_portal.
public sealed class PhoneNetworkCollector : ICollector
{
    private readonly Context _ctx;
    private readonly Uploader? _uploader;
    public string Surface => "phone.network";

    public PhoneNetworkCollector(Context ctx, Uploader? uploader = null)
    {
        _ctx = ctx;
        _uploader = uploader;
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var cm = (ConnectivityManager?)_ctx.GetSystemService(Context.ConnectivityService);
        if (cm is null) yield break;

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void Push(string trigger, Network? net, NetworkCapabilities? caps)
        {
            var snap = Snapshot(trigger, net, caps);
            _uploader?.SetNetworkProfile(isMetered: snap.metered);
            channel.Writer.TryWrite(new CollectedEvent(Surface,
                JsonSerializer.Serialize(snap), DateTimeOffset.UtcNow));
        }

        var callback = new Callback(Push);
        var request = new NetworkRequest.Builder()
            ?.AddCapability(NetCapability.Internet)
            ?.Build();
        if (request is not null) cm.RegisterNetworkCallback(request, callback);

        // Startup snapshot from the currently-active network.
        var active = cm.ActiveNetwork;
        var activeCaps = active is null ? null : cm.GetNetworkCapabilities(active);
        Push("startup", active, activeCaps);

        using var reg = ct.Register(() =>
        {
            try { cm.UnregisterNetworkCallback(callback); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private record Snap(string trigger, bool available, bool metered, bool validated,
        bool roaming, bool captive_portal, string transport, string timestamp);

    private static dynamic Snapshot(string trigger, Network? net, NetworkCapabilities? caps)
    {
        var transport = caps switch
        {
            null => "unknown",
            _ when caps.HasTransport(TransportType.Wifi) => "wifi",
            _ when caps.HasTransport(TransportType.Cellular) => "cellular",
            _ when caps.HasTransport(TransportType.Ethernet) => "ethernet",
            _ when caps.HasTransport(TransportType.Vpn) => "vpn",
            _ when caps.HasTransport(TransportType.Bluetooth) => "bluetooth",
            _ => "other",
        };
        var validated = caps?.HasCapability(NetCapability.Validated) ?? false;
        var metered = !(caps?.HasCapability(NetCapability.NotMetered) ?? false);
        var roaming = !(caps?.HasCapability(NetCapability.NotRoaming) ?? true);
        var captive = caps?.HasCapability(NetCapability.CaptivePortal) ?? false;
        return new
        {
            trigger,
            available = net is not null,
            metered,
            validated,
            roaming,
            captive_portal = captive,
            transport,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    private sealed class Callback : ConnectivityManager.NetworkCallback
    {
        private readonly Action<string, Network?, NetworkCapabilities?> _push;
        public Callback(Action<string, Network?, NetworkCapabilities?> push) => _push = push;

        public override void OnAvailable(Network network) => _push("available", network, null);
        public override void OnLost(Network network) => _push("lost", null, null);
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities)
            => _push("capabilities_changed", network, networkCapabilities);
        public override void OnUnavailable() => _push("unavailable", null, null);
    }
}
