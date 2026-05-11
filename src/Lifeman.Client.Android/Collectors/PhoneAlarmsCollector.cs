using System.Text.Json;
using Android.App;
using Android.Content;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.alarms` — the user's next-scheduled alarm clock. Read via
/// AlarmManager.GetNextAlarmClock (no permission needed) and refreshed
/// on ACTION_NEXT_ALARM_CLOCK_CHANGED. No polling. The kernel can use
/// this to anticipate wake-time and schedule "good morning" outputs.
public sealed class PhoneAlarmsCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.alarms";

    public PhoneAlarmsCollector(Context ctx) => _ctx = ctx;

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            var am = (AlarmManager?)_ctx.GetSystemService(Context.AlarmService);
            if (am is null) return ChannelCollectorScaffold.Teardown(() => { });

            CollectedEvent Build(string trigger)
            {
                var info = am.NextAlarmClock;
                var payload = JsonSerializer.Serialize(new
                {
                    trigger,
                    has_alarm = info is not null,
                    trigger_time_ms = info?.TriggerTime,
                    trigger_at = info is null ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(info.TriggerTime).ToString("O"),
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                });
                return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
            }

            var receiver = new ActionBroadcastReceiver(_ => emit(Build("alarm_clock_changed")));
            _ctx.RegisterReceiver(receiver,
                new IntentFilter(AlarmManager.ActionNextAlarmClockChanged));

            emit(Build("startup"));

            return ChannelCollectorScaffold.Teardown(
                () => { try { _ctx.UnregisterReceiver(receiver); } catch { } });
        }, ct);
}
