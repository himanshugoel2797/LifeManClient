using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Lifeman.Client.Android.Collectors;
using Lifeman.Client.Android.Config;
using Lifeman.Client.Android.Renderers;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Hosting;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;

namespace Lifeman.Client.Android.Services;

/// The agent's only long-lived component. Runs the shared
/// LifemanClientHost (outbox → uploader → SSE → renderer) and stays
/// alive across Doze via a foreground service. The "lifeman is
/// observing" notification is mandatory on Android 13+ and intentional
/// per the design — users should always know the agent is watching.
[Service(
    Enabled = true,
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class LifemanService : Service
{
    public const string ChannelId = "lifeman.service";
    public const string ChannelName = "Lifeman agent";
    public const string ActionStart = "dev.lifeman.client.START";
    public const string ActionStop = "dev.lifeman.client.STOP";
    private const int NotificationId = 1;

    private CancellationTokenSource? _cts;
    private Task? _hostTask;
    private HttpClient? _http;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            ShutdownInternal();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (_hostTask is not null) return StartCommandResult.Sticky;

        StartForeground(NotificationId, BuildPersistentNotification());
        _cts = new CancellationTokenSource();
        _hostTask = Task.Run(() => RunHostAsync(_cts.Token));
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        ShutdownInternal();
        base.OnDestroy();
    }

    private async Task RunHostAsync(CancellationToken ct)
    {
        try
        {
            var stateDir = FilesDir!.AbsolutePath;
            var config = new KeystoreConfigStore(ApplicationContext!);
            if (await config.GetAsync(ConfigKeys.DeviceToken, ct).ConfigureAwait(false) is null)
            {
                global::Android.Util.Log.Warn("lifeman", "no device token; service stopping");
                StopSelf();
                return;
            }

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await using var outbox = new SqliteOutbox(System.IO.Path.Combine(stateDir, "outbox.db"));
            var lifemanHttp = new LifemanHttpClient(_http, config);
            var uploader = new Uploader(outbox, lifemanHttp, config,
                options: new UploaderOptions { IdlePollInterval = TimeSpan.FromSeconds(5) });
            // Phones are typically metered or treated as such — start
            // with large batches to amortise radio wakeups. The kernel
            // can push us toward smaller batches as it sees fit.
            uploader.SetNetworkProfile(isMetered: true);

            var sse = new SseReceiver(lifemanHttp, config);
            var responses = new OutputResponseClient(lifemanHttp, config);
            var renderer = new AndroidNotificationRenderer(ApplicationContext!);

            var collectors = new List<ICollector>
            {
                new PhoneBatteryCollector(ApplicationContext!),
            };

            await using var host = new LifemanClientHost(outbox, uploader, sse, renderer, collectors);
            await host.RunAsync(ct).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("lifeman", $"host crashed: {ex}");
        }
    }

    private void ShutdownInternal()
    {
        try { _cts?.Cancel(); } catch { }
        try { _hostTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _http?.Dispose(); } catch { }
        _cts = null; _hostTask = null; _http = null;
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr is null || mgr.GetNotificationChannel(ChannelId) is not null) return;
        // Low importance: persistent ongoing notification shouldn't ding.
        var ch = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
        {
            Description = "Persistent indicator that the lifeman agent is running.",
        };
        ch.SetShowBadge(false);
        mgr.CreateNotificationChannel(ch);
    }

    private Notification BuildPersistentNotification()
    {
        var stopIntent = new Intent(this, typeof(LifemanService)).SetAction(ActionStop);
        var stopPending = PendingIntent.GetService(this, 0, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Lifeman is observing")
            .SetContentText("Agent is running in the background.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuInfoDetails)
            .SetOngoing(true)
            .SetPriority((int)NotificationPriority.Low)
            .AddAction(0, "Stop", stopPending)
            .Build()!;
    }

    public static void Start(Context ctx)
    {
        var intent = new Intent(ctx, typeof(LifemanService)).SetAction(ActionStart);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
        else ctx.StartService(intent);
    }

    public static void Stop(Context ctx)
    {
        var intent = new Intent(ctx, typeof(LifemanService)).SetAction(ActionStop);
        ctx.StartService(intent);
    }
}
