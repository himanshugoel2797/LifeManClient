using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Microsoft.Win32;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.locale` — locale + timezone snapshot, refreshed on the OS
/// events that announce changes. Mirror of `phone.locale` on Android;
/// the kernel uses these for time-aware framings ("good morning" in
/// the user's tz, currency-aware money summaries, etc.).
[SupportedOSPlatform("windows")]
public sealed class DesktopLocaleCollector : ICollector
{
    public string Surface => "desktop.locale";

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            emit(Snapshot("startup"));

            UserPreferenceChangedEventHandler pref = (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.Locale)
                    emit(Snapshot("locale_changed"));
            };
            EventHandler time = (_, _) =>
            {
                // SystemEvents.TimeChanged covers timezone changes too —
                // both Windows-side time apply and tz switch raise it.
                TimeZoneInfo.ClearCachedData();
                emit(Snapshot("time_changed"));
            };
            SystemEvents.UserPreferenceChanged += pref;
            SystemEvents.TimeChanged += time;

            return ChannelCollectorScaffold.Teardown(() =>
            {
                SystemEvents.UserPreferenceChanged -= pref;
                SystemEvents.TimeChanged -= time;
            });
        }, ct);

    private static CollectedEvent Snapshot(string trigger)
    {
        var culture = CultureInfo.CurrentCulture;
        var ui = CultureInfo.CurrentUICulture;
        var tz = TimeZoneInfo.Local;
        var offset = tz.GetUtcOffset(DateTimeOffset.UtcNow);
        var payload = JsonSerializer.Serialize(new
        {
            trigger,
            locale = culture.Name,
            ui_locale = ui.Name,
            language = culture.TwoLetterISOLanguageName,
            region = new RegionInfo(culture.Name.Length == 0 ? "en-US" : culture.Name).TwoLetterISORegionName,
            timezone_id = tz.Id,
            timezone_name = tz.DisplayName,
            utc_offset_minutes = (int)offset.TotalMinutes,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.locale", payload, DateTimeOffset.UtcNow);
    }
}
