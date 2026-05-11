using System.Text.Json;
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

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            CollectedEvent Build(string trigger)
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
                return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
            }

            var receiver = new ActionBroadcastReceiver(intent =>
            {
                switch (intent.Action)
                {
                    case Intent.ActionLocaleChanged: emit(Build("locale_changed")); break;
                    case Intent.ActionTimezoneChanged: emit(Build("timezone_changed")); break;
                }
            });
            var filter = new IntentFilter();
            filter.AddAction(Intent.ActionLocaleChanged);
            filter.AddAction(Intent.ActionTimezoneChanged);
            _ctx.RegisterReceiver(receiver, filter);

            emit(Build("startup"));

            return ChannelCollectorScaffold.Teardown(
                () => { try { _ctx.UnregisterReceiver(receiver); } catch { } });
        }, ct);
}
