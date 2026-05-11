using System.Text.Json;
using System.Threading.Channels;
using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Lifeman.Client.Android.Services;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.media` — active media sessions and playback transitions
/// (play / pause / stop, track changes). Uses MediaSessionManager,
/// which requires the same Notification Listener access as our
/// notification collector — once that's enabled the OS lets us
/// enumerate every media session system-wide.
///
/// Battery cost: callback-driven (OnPlaybackStateChanged,
/// OnMetadataChanged, OnSessionDestroyed). No polling. The session
/// list itself is refreshed only on OnActiveSessionsChanged callbacks.
public sealed class PhoneMediaCollector : ICollector
{
    private readonly Context _ctx;
    public string Surface => "phone.media";

    public PhoneMediaCollector(Context ctx) => _ctx = ctx;

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!LifemanNotificationListener.IsEnabled(_ctx))
        {
            global::Android.Util.Log.Info("lifeman",
                "phone.media: notification access not granted, collector idle");
            yield break;
        }

        var msm = (MediaSessionManager?)_ctx.GetSystemService(Context.MediaSessionService);
        if (msm is null) yield break;
        var component = LifemanNotificationListener.ComponentName(_ctx);

        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var controllerTracking = new Dictionary<MediaSession.Token, MediaSessionTracker>();

        void RefreshSessions()
        {
            IList<global::Android.Media.Session.MediaController>? controllers = null;
            try { controllers = msm.GetActiveSessions(component); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("lifeman", $"phone.media: GetActiveSessions failed: {ex.Message}");
                return;
            }
            var live = new HashSet<MediaSession.Token>();
            foreach (var c in controllers ?? Array.Empty<global::Android.Media.Session.MediaController>())
            {
                if (c.SessionToken is null) continue;
                live.Add(c.SessionToken);
                if (!controllerTracking.ContainsKey(c.SessionToken))
                {
                    var tracker = new MediaSessionTracker(c, ev => channel.Writer.TryWrite(ev));
                    controllerTracking[c.SessionToken] = tracker;
                    tracker.Attach();
                    tracker.PushSnapshot("session_added");
                }
            }
            // Drop trackers whose sessions are no longer in the active list.
            foreach (var token in controllerTracking.Keys.ToArray())
            {
                if (!live.Contains(token))
                {
                    controllerTracking[token].PushSnapshot("session_removed");
                    controllerTracking[token].Detach();
                    controllerTracking.Remove(token);
                }
            }
        }

        // Dedicated HandlerThread so the OS callbacks (session
        // changes, playback/metadata callbacks) dispatch on a worker
        // looper we own — not on the app's main UI thread, where they
        // would compete with input handling and animations.
        var handlerThread = new HandlerThread("lifeman-media");
        handlerThread.Start();
        var handler = new Handler(handlerThread.Looper!);

        var listener = new SessionsChangedListener(RefreshSessions);
        msm.AddOnActiveSessionsChangedListener(listener, component, handler);
        RefreshSessions();

        using var reg = ct.Register(() =>
        {
            try { msm.RemoveOnActiveSessionsChangedListener(listener); } catch { }
            foreach (var t in controllerTracking.Values) t.Detach();
            controllerTracking.Clear();
            try { handlerThread.QuitSafely(); } catch { }
            channel.Writer.TryComplete();
        });

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    private sealed class SessionsChangedListener : Java.Lang.Object,
        MediaSessionManager.IOnActiveSessionsChangedListener
    {
        private readonly Action _onChange;
        public SessionsChangedListener(Action onChange) => _onChange = onChange;
        public void OnActiveSessionsChanged(IList<global::Android.Media.Session.MediaController>? controllers) => _onChange();
    }

    private sealed class MediaSessionTracker
    {
        private readonly global::Android.Media.Session.MediaController _controller;
        private readonly Action<CollectedEvent> _emit;
        private readonly TrackerCallback _callback;

        public MediaSessionTracker(global::Android.Media.Session.MediaController controller, Action<CollectedEvent> emit)
        {
            _controller = controller;
            _emit = emit;
            _callback = new TrackerCallback(this);
        }

        public void Attach() => _controller.RegisterCallback(_callback);
        public void Detach() { try { _controller.UnregisterCallback(_callback); } catch { } }

        public void PushSnapshot(string trigger)
        {
            var meta = _controller.Metadata;
            var state = _controller.PlaybackState;
            var payload = JsonSerializer.Serialize(new
            {
                trigger,
                package = _controller.PackageName,
                state = state?.State.ToString(),
                position_ms = state?.Position,
                playback_speed = state?.PlaybackSpeed,
                title = meta?.GetString(MediaMetadata.MetadataKeyTitle),
                artist = meta?.GetString(MediaMetadata.MetadataKeyArtist),
                album = meta?.GetString(MediaMetadata.MetadataKeyAlbum),
                duration_ms = meta?.GetLong(MediaMetadata.MetadataKeyDuration),
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            _emit(new CollectedEvent("phone.media", payload, DateTimeOffset.UtcNow));
        }

        private sealed class TrackerCallback : global::Android.Media.Session.MediaController.Callback
        {
            private readonly MediaSessionTracker _t;
            public TrackerCallback(MediaSessionTracker t) => _t = t;
            public override void OnPlaybackStateChanged(PlaybackState? state) => _t.PushSnapshot("playback_state");
            public override void OnMetadataChanged(MediaMetadata? metadata) => _t.PushSnapshot("metadata");
            public override void OnSessionDestroyed() => _t.PushSnapshot("session_destroyed");
        }
    }
}
