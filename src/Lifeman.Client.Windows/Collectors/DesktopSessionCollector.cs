using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
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

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        channel.Writer.TryWrite(Emit("startup"));

        SessionSwitchEventHandler handler = (_, e) =>
            channel.Writer.TryWrite(Emit(e.Reason.ToString().ToLowerInvariant()));
        SystemEvents.SessionSwitch += handler;
        using var reg = ct.Register(() =>
        {
            SystemEvents.SessionSwitch -= handler;
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

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
