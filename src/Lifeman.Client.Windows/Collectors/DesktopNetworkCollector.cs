using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Lifeman.Client.Collectors;
using Lifeman.Client.Net;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.network` — emits on connectivity / address changes and on
/// startup. Also probes `INetworkCostManager` to decide whether the
/// current connection is metered (cellular tether, Wi-Fi marked
/// metered, …) and pushes that down to the Uploader so it switches to
/// large/cheap batches.
///
/// COM cost-manager call is best-effort: any failure (older Windows,
/// service stopped) downgrades silently to `metered=false`, which is
/// the safe default.
[SupportedOSPlatform("windows")]
public sealed class DesktopNetworkCollector : ICollector
{
    public string Surface => "desktop.network";

    private readonly Uploader? _uploader;

    public DesktopNetworkCollector(Uploader? uploader = null) => _uploader = uploader;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void Push(string trigger)
        {
            var snap = Read();
            _uploader?.SetNetworkProfile(snap.Metered);
            channel.Writer.TryWrite(new CollectedEvent("desktop.network",
                JsonSerializer.Serialize(new
                {
                    trigger,
                    available = snap.Available,
                    metered = snap.Metered,
                    over_data_limit = snap.OverDataLimit,
                    roaming = snap.Roaming,
                    interface_type = snap.InterfaceType,
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                }),
                DateTimeOffset.UtcNow));
        }

        Push("startup");

        NetworkAvailabilityChangedEventHandler avail = (_, __) => Push("availability");
        NetworkAddressChangedEventHandler addr = (_, __) => Push("address");
        NetworkChange.NetworkAvailabilityChanged += avail;
        NetworkChange.NetworkAddressChanged += addr;

        using var reg = ct.Register(() =>
        {
            NetworkChange.NetworkAvailabilityChanged -= avail;
            NetworkChange.NetworkAddressChanged -= addr;
            channel.Writer.TryComplete();
        });

        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    private readonly record struct Snap(
        bool Available, bool Metered, bool OverDataLimit, bool Roaming, string InterfaceType);

    private static Snap Read()
    {
        var available = NetworkInterface.GetIsNetworkAvailable();
        var iface = "unknown";
        try
        {
            iface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.NetworkInterfaceType.ToString())
                .FirstOrDefault() ?? "unknown";
        }
        catch { }

        var cost = TryReadCost();
        return new Snap(available, cost.Metered, cost.OverDataLimit, cost.Roaming, iface);
    }

    private readonly record struct Cost(bool Metered, bool OverDataLimit, bool Roaming);

    [Flags]
    private enum NlmConnectionCost : uint
    {
        Unrestricted = 0x1,
        Fixed = 0x2,
        Variable = 0x4,
        OverDataLimit = 0x10000,
        Congested = 0x20000,
        Roaming = 0x40000,
        Approaching = 0x80000,
    }

    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        [PreserveSig] int GetCost(out uint pCost, IntPtr pDestIPaddr);
    }

    private static Cost TryReadCost()
    {
        try
        {
            var clsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
            var type = Type.GetTypeFromCLSID(clsid);
            if (type is null) return default;
            var inst = Activator.CreateInstance(type);
            if (inst is not INetworkCostManager mgr) return default;
            if (mgr.GetCost(out var cost, IntPtr.Zero) != 0) return default;
            var c = (NlmConnectionCost)cost;
            // Fixed / Variable both indicate "metered" semantics — they
            // cost the user per byte or have a cap.
            var metered = c.HasFlag(NlmConnectionCost.Fixed)
                       || c.HasFlag(NlmConnectionCost.Variable)
                       || c.HasFlag(NlmConnectionCost.OverDataLimit);
            return new Cost(metered, c.HasFlag(NlmConnectionCost.OverDataLimit), c.HasFlag(NlmConnectionCost.Roaming));
        }
        catch { return default; }
    }
}
