using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Lifeman.Client.Collectors;
using Microsoft.Win32;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.power` — emits an event on power-mode change (AC ↔ battery,
/// suspend, resume) plus an initial snapshot on start. No permissions
/// required, no API surface beyond stock Win32.
[SupportedOSPlatform("windows")]
public sealed class DesktopPowerCollector : ICollector
{
    public string Surface => "desktop.power";

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        channel.Writer.TryWrite(Snapshot("startup"));

        PowerModeChangedEventHandler handler = (_, e) =>
        {
            channel.Writer.TryWrite(Snapshot(e.Mode.ToString().ToLowerInvariant()));
        };
        SystemEvents.PowerModeChanged += handler;
        using var reg = ct.Register(() =>
        {
            SystemEvents.PowerModeChanged -= handler;
            channel.Writer.TryComplete();
        });

        await foreach (var ev in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    private static CollectedEvent Snapshot(string trigger)
    {
        var status = PowerStatus.Read();
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            on_ac = status.OnAc,
            battery_level = status.BatteryLifePercent,
            battery_charging = status.Charging,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.power", payload, DateTimeOffset.UtcNow);
    }
}

[SupportedOSPlatform("windows")]
internal static class PowerStatus
{
    public sealed record Snapshot(bool? OnAc, float? BatteryLifePercent, bool? Charging);

    public static Snapshot Read()
    {
        var s = new Native.SYSTEM_POWER_STATUS();
        if (!Native.GetSystemPowerStatus(ref s)) return new Snapshot(null, null, null);
        bool? onAc = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        float? pct = s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent / 100f;
        bool? charging = (s.BatteryFlag & 0x08) != 0;
        return new Snapshot(onAc, pct, charging);
    }

    private static class Native
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        public static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);
    }
}
