using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Lifeman.Client.Android.Collectors;
using Lifeman.Client.Android.Config;
using Lifeman.Client.Android.Logging;
using Lifeman.Client.Android.Renderers;
using Lifeman.Client.Collectors;
using Lifeman.Client.Config;
using Lifeman.Client.Hosting;
using Lifeman.Client.Net;
using Lifeman.Client.Outbox;
using Microsoft.Extensions.Logging;

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
    private ILoggerFactory? _loggerFactory;
    private static volatile bool s_running;
    public static bool IsRunning(Context _) => s_running;

    /// Live outbox reference while the host loop is running. BroadcastReceivers
    /// fired by toast / notification dismiss intents use this to enqueue
    /// `client.output_event` side-channel events without re-opening SQLite.
    public static IOutbox? CurrentOutbox { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        global::Android.Util.Log.Info("lifeman", $"Service.OnStartCommand action={intent?.Action}");
        if (intent?.Action == ActionStop)
        {
            ShutdownInternal();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        if (_hostTask is not null) return StartCommandResult.Sticky;

        StartForeground(NotificationId, BuildPersistentNotification());
        s_running = true;
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
        global::Android.Util.Log.Info("lifeman", "RunHostAsync: entered");
        try
        {
            var stateDir = FilesDir!.AbsolutePath;
            global::Android.Util.Log.Info("lifeman", $"RunHostAsync: stateDir={stateDir}");
            var config = new KeystoreConfigStore(ApplicationContext!);
            var token = await config.GetAsync(ConfigKeys.DeviceToken, ct).ConfigureAwait(false);
            global::Android.Util.Log.Info("lifeman", $"RunHostAsync: token present={token is not null}");
            if (token is null)
            {
                global::Android.Util.Log.Warn("lifeman", "no device token; service stopping");
                StopSelf();
                return;
            }

            _loggerFactory = LoggerFactory.Create(b => b
                .AddProvider(new AndroidLogcatLoggerProvider())
                .SetMinimumLevel(LogLevel.Information));

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            global::Android.Util.Log.Info("lifeman", "RunHostAsync: creating outbox");
            await using var outbox = new SqliteOutbox(System.IO.Path.Combine(stateDir, "outbox.db"));
            CurrentOutbox = outbox;
            global::Android.Util.Log.Info("lifeman", "RunHostAsync: wiring host");
            var lifemanHttp = new LifemanHttpClient(_http, config);
            var uploader = new Uploader(outbox, lifemanHttp, config,
                options: new UploaderOptions { IdlePollInterval = TimeSpan.FromSeconds(5) },
                logger: _loggerFactory.CreateLogger<Uploader>());
            uploader.SetNetworkProfile(isMetered: true);

            var sse = new SseReceiver(lifemanHttp, config,
                logger: _loggerFactory.CreateLogger<SseReceiver>());
            var responses = new OutputResponseClient(lifemanHttp, config);
            var renderer = new AndroidNotificationRenderer(ApplicationContext!);

            var ctx = ApplicationContext!;
            // Collectors gated by permissions self-disable to no-op
            // generators if their grant is missing, so it's safe to
            // include them unconditionally — they just stay quiet until
            // the user opens the permission helpers in MainActivity.
            var collectors = new List<ICollector>
            {
                new HeartbeatCollector(TimeSpan.FromMinutes(5)),
                new PhoneBatteryCollector(ctx),
                new PhoneScreenCollector(ctx),
                new PhoneIdleCollector(ctx),
                new PhoneNetworkCollector(ctx, uploader),
                new PhoneHeadphonesCollector(ctx),
                new PhoneAlarmsCollector(ctx),
                new PhoneLocaleCollector(ctx),
                new PhoneForegroundAppCollector(ctx),       // needs PACKAGE_USAGE_STATS
                new PhoneNotificationCollector(ctx, config),// needs Notification access
                new PhoneMediaCollector(ctx),               // needs Notification access
                new PhoneCalendarCollector(ctx),            // needs READ_CALENDAR
                new PhoneLocationCollector(ctx),            // needs ACCESS_FINE_LOCATION
                new PhoneBluetoothAudioCollector(ctx),      // needs BLUETOOTH_CONNECT (S+)
            };

            await using var host = new LifemanClientHost(outbox, uploader, sse, renderer, collectors,
                _loggerFactory.CreateLogger<LifemanClientHost>());
            global::Android.Util.Log.Info("lifeman", "RunHostAsync: host.RunAsync starting");
            await host.RunAsync(ct).ConfigureAwait(false);
            global::Android.Util.Log.Info("lifeman", "RunHostAsync: host.RunAsync returned");
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
        try { _loggerFactory?.Dispose(); } catch { }
        _cts = null; _hostTask = null; _http = null; _loggerFactory = null;
        CurrentOutbox = null;
        s_running = false;
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
