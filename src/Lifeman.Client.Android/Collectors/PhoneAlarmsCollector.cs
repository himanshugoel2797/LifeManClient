using System.Text.Json;
using System.Threading.Channels;
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

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var am = (AlarmManager?)_ctx.GetSystemService(Context.AlarmService);
        if (am is null) yield break;

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void Push(string trigger)
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
            channel.Writer.TryWrite(new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow));
        }

        var receiver = new AlarmReceiver(() => Push("alarm_clock_changed"));
        _ctx.RegisterReceiver(receiver,
            new IntentFilter(AlarmManager.ActionNextAlarmClockChanged));

        Push("startup");

        using var reg = ct.Register(() =>
        {
            try { _ctx.UnregisterReceiver(receiver); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private sealed class AlarmReceiver : BroadcastReceiver
    {
        private readonly Action _onChange;
        public AlarmReceiver(Action onChange) => _onChange = onChange;
        public override void OnReceive(Context? context, Intent? intent) => _onChange();
    }
}
