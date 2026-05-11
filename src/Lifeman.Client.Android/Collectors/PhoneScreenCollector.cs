using System.Text.Json;
using System.Threading.Channels;
using Android.App;
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

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var receiver = new ScreenReceiver((trigger, interactive) =>
            channel.Writer.TryWrite(Emit(trigger, interactive)));

        var filter = new IntentFilter();
        filter.AddAction(Intent.ActionScreenOn);
        filter.AddAction(Intent.ActionScreenOff);
        filter.AddAction(Intent.ActionUserPresent);
        _ctx.RegisterReceiver(receiver, filter);

        // Startup snapshot. PowerManager.IsInteractive returns true if
        // screen is on regardless of lock state — matches what the
        // ScreenOn broadcast would have told us at boot.
        var pm = (PowerManager?)_ctx.GetSystemService(Context.PowerService);
        var interactive = pm?.IsInteractive ?? true;
        channel.Writer.TryWrite(Emit("startup", interactive));

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private CollectedEvent Emit(string trigger, bool interactive)
    {
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            interactive,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
    }

    private sealed class ScreenReceiver : BroadcastReceiver
    {
        private readonly Action<string, bool> _onChange;
        public ScreenReceiver(Action<string, bool> onChange) => _onChange = onChange;

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action is null) return;
            switch (intent.Action)
            {
                case Intent.ActionScreenOn: _onChange("screen_on", true); break;
                case Intent.ActionScreenOff: _onChange("screen_off", false); break;
                case Intent.ActionUserPresent: _onChange("user_present", true); break;
            }
        }
    }
}
