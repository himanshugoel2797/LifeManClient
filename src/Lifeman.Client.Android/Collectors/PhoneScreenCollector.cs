using System.Text.Json;
using Android.Content;
using Android.OS;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.screen` — screen on/off and user-unlock transitions.
/// Pure broadcast receivers, zero polling, no permission surface.
/// Emits a startup snapshot of the current interactive state via
/// PowerManager so the kernel never has to guess from "I haven't
/// heard anything yet" whether the screen is on.
public sealed class PhoneScreenCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.screen";

    public PhoneScreenCollector(Context ctx) => _ctx = ctx;

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            var receiver = new ActionBroadcastReceiver(intent =>
            {
                switch (intent.Action)
                {
                    case Intent.ActionScreenOn: emit(Build("screen_on", true)); break;
                    case Intent.ActionScreenOff: emit(Build("screen_off", false)); break;
                    case Intent.ActionUserPresent: emit(Build("user_present", true)); break;
                }
            });

            var filter = new IntentFilter();
            filter.AddAction(Intent.ActionScreenOn);
            filter.AddAction(Intent.ActionScreenOff);
            filter.AddAction(Intent.ActionUserPresent);
            _ctx.RegisterReceiver(receiver, filter);

            // Startup snapshot. PowerManager.IsInteractive returns true if
            // screen is on regardless of lock state — matches what the
            // ScreenOn broadcast would have told us at boot.
            var pm = (PowerManager?)_ctx.GetSystemService(Context.PowerService);
            emit(Build("startup", pm?.IsInteractive ?? true));

            return ChannelCollectorScaffold.Teardown(
                () => { try { _ctx.UnregisterReceiver(receiver); } catch { } });
        }, ct);

    private CollectedEvent Build(string trigger, bool interactive)
    {
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            interactive,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }
}
