using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Content.Res;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.locale` + `phone.timezone` rolled into one collector since
/// they're both nearly-free broadcasts and the kernel typically wants
/// both whenever either changes (e.g. flying to another country flips
/// both simultaneously). One surface, "locale_changed" or
/// "timezone_changed" trigger field disambiguates.
public sealed class PhoneLocaleCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.locale";

    public PhoneLocaleCollector(Context ctx) => _ctx = ctx;

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
            var locale = Resources.System!.Configuration!.Locales!.Get(0);
            var tz = Java.Util.TimeZone.Default!;
            var payload = JsonSerializer.Serialize(new
            {
                trigger,
                locale = locale?.ToLanguageTag(),
                language = locale?.Language,
                country = locale?.Country,
                timezone_id = tz.ID,
                utc_offset_minutes = tz.RawOffset / (60 * 1000),
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            channel.Writer.TryWrite(new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow));
        }

        var receiver = new LocaleReceiver(Push);
        var filter = new IntentFilter();
        filter.AddAction(Intent.ActionLocaleChanged);
        filter.AddAction(Intent.ActionTimezoneChanged);
        _ctx.RegisterReceiver(receiver, filter);

        Push("startup");

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private sealed class LocaleReceiver : BroadcastReceiver
    {
        private readonly Action<string> _onChange;
        public LocaleReceiver(Action<string> onChange) => _onChange = onChange;
        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case Intent.ActionLocaleChanged: _onChange("locale_changed"); break;
                case Intent.ActionTimezoneChanged: _onChange("timezone_changed"); break;
            }
        }
    }
}
