using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Microsoft.Win32;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.battery` — laptop battery level + charge state. The
/// existing `desktop.power` collector only emits on AC↔battery mode
/// transitions; this is the dual-purpose "what % am I at right now"
/// collector. Polls on a low-frequency timer plus a fast-path on
/// `SystemEvents.PowerModeChanged` so unplug/plug events surface
/// without waiting for the next poll. Self-disables on desktops that
/// have no battery.
[SupportedOSPlatform("windows")]
public sealed class DesktopBatteryCollector : ICollector
{
    public string Surface => "desktop.battery";

    private readonly TimeSpan _interval;
    public DesktopBatteryCollector(TimeSpan? interval = null)
    {
        _interval = interval ?? TimeSpan.FromMinutes(2);
    }

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            // Desktop / VM with no battery? PowerStatus reports
            // BatteryChargeStatus.NoSystemBattery — self-disable cleanly.
            if (SystemInformation.PowerStatus.BatteryChargeStatus
                .HasFlag(BatteryChargeStatus.NoSystemBattery))
            {
                emit(ClientObservations.CollectorDisabled(Surface, "no system battery"));
                return ChannelCollectorScaffold.Teardown(() => { });
            }

            emit(Snapshot("startup"));
            PowerModeChangedEventHandler handler = (_, e) =>
                emit(Snapshot(e.Mode.ToString().ToLowerInvariant()));
            SystemEvents.PowerModeChanged += handler;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(_interval, cts.Token).ConfigureAwait(false);
                        emit(Snapshot("periodic"));
                    }
                }
                catch (OperationCanceledException) { }
            });

            return ChannelCollectorScaffold.Teardown(() =>
            {
                SystemEvents.PowerModeChanged -= handler;
                cts.Cancel();
                cts.Dispose();
            });
        }, ct);

    private static CollectedEvent Snapshot(string trigger)
    {
        var ps = SystemInformation.PowerStatus;
        var pct = ps.BatteryLifePercent;
        var onAc = ps.PowerLineStatus == PowerLineStatus.Online;
        var charging = ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging);
        var low = ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Low)
                  || ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Critical);
        // BatteryLifeRemaining is in seconds, -1 if unknown.
        int? remainingSeconds = ps.BatteryLifeRemaining >= 0 ? ps.BatteryLifeRemaining : null;
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            on_ac = onAc,
            battery_level = (pct >= 0 && pct <= 1) ? (float?)pct : null,
            battery_charging = charging,
            battery_low = low,
            remaining_seconds = remainingSeconds,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.battery", payload, DateTimeOffset.UtcNow);
    }
}
