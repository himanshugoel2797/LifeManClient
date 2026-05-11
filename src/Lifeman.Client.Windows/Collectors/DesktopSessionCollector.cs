using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Microsoft.Win32;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.session` — lock / unlock, console connect / disconnect,
/// remote connect / disconnect transitions. SystemEvents.SessionSwitch
/// fires from a hidden message-loop thread the assembly installs for
/// us; no polling, no extra wakeups beyond what Windows already pays.
[SupportedOSPlatform("windows")]
public sealed class DesktopSessionCollector : ICollector
{
    public string Surface => "desktop.session";

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            emit(Emit("startup"));

            SessionSwitchEventHandler handler = (_, e) =>
                emit(Emit(e.Reason.ToString().ToLowerInvariant()));
            SystemEvents.SessionSwitch += handler;

            return ChannelCollectorScaffold.Teardown(
                () => SystemEvents.SessionSwitch -= handler);
        }, ct);

    private static CollectedEvent Emit(string trigger)
    {
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.session", payload, DateTimeOffset.UtcNow);
    }
}
