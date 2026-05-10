using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Lifeman.Client.Contracts;
using Lifeman.Client.Net;
using Lifeman.Client.Renderers;

namespace Lifeman.Client.Android.Renderers;

/// Renders kernel `output.deliver` events as Android notifications.
/// Action buttons fire a PendingIntent into ActionResponseReceiver
/// which POSTs to /api/outputs/{id}/respond.
public sealed class AndroidNotificationRenderer : IRenderer
{
    public const string ChannelId = "lifeman.output";
    public const string ChannelName = "Lifeman outputs";
    public const string ChannelDescription = "Surfaces from the lifeman kernel.";

    private readonly Context _ctx;
    private readonly ConcurrentDictionary<string, int> _ids = new();
    private static int _idSeed = 1000;

    public AndroidNotificationRenderer(Context ctx)
    {
        _ctx = ctx;
        EnsureChannel(ctx);
    }

    public static void EnsureChannel(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var mgr = (NotificationManager?)ctx.GetSystemService(Context.NotificationService);
        if (mgr is null) return;
        if (mgr.GetNotificationChannel(ChannelId) is not null) return;
        var channel = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Default)
        {
            Description = ChannelDescription,
        };
        mgr.CreateNotificationChannel(channel);
    }

    public Task ShowAsync(OutputDeliver deliver, CancellationToken ct)
    {
        var builder = new NotificationCompat.Builder(_ctx, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentTitle(deliver.Content.Title ?? deliver.Category)
            .SetContentText(deliver.Content.Body ?? string.Empty)
            .SetAutoCancel(true);

        foreach (var action in deliver.Actions)
        {
            var intent = new Intent(_ctx, typeof(ActionResponseReceiver))
                .SetAction(ActionResponseReceiver.IntentAction)
                .PutExtra(ActionResponseReceiver.ExtraOutputId, deliver.OutputId)
                .PutExtra(ActionResponseReceiver.ExtraAction, action.Label);
            var flags = PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;
            var requestCode = HashCode.Combine(deliver.OutputId, action.Label);
            var pending = PendingIntent.GetBroadcast(_ctx, requestCode, intent, flags);
            builder.AddAction(0, action.Label, pending);
        }

        var id = _ids.GetOrAdd(deliver.OutputId, _ => Interlocked.Increment(ref _idSeed));
        NotificationManagerCompat.From(_ctx).Notify(id, builder.Build()!);
        return Task.CompletedTask;
    }

    public Task DismissAsync(string outputId, CancellationToken ct)
    {
        if (_ids.TryRemove(outputId, out var id))
            NotificationManagerCompat.From(_ctx).Cancel(id);
        return Task.CompletedTask;
    }
}

/// Receives notification action button taps and forwards them to the
/// kernel. The renderer can't capture an HttpClient via the
/// PendingIntent because PendingIntents are restored across process
/// restarts (e.g. tap a notification after the OS killed the app) — so
/// the receiver builds its own HttpClient + config on demand.
[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class ActionResponseReceiver : BroadcastReceiver
{
    public const string IntentAction = "dev.lifeman.client.OUTPUT_ACTION";
    public const string ExtraOutputId = "output_id";
    public const string ExtraAction = "action";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var outputId = intent.GetStringExtra(ExtraOutputId);
        var label = intent.GetStringExtra(ExtraAction);
        if (string.IsNullOrEmpty(outputId) || string.IsNullOrEmpty(label)) return;

        // The system holds a wakelock on us for ~10s during OnReceive;
        // that's plenty for a single small POST. Fire-and-forget.
        var pendingResult = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                var config = new Config.KeystoreConfigStore(context.ApplicationContext!);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var lh = new global::Lifeman.Client.Net.LifemanHttpClient(http, config);
                var responses = new OutputResponseClient(lh, config);
                await responses.RespondAsync(outputId, label).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("lifeman", $"respond failed: {ex.Message}");
            }
            finally
            {
                try { pendingResult?.Finish(); } catch { }
            }
        });
    }
}
