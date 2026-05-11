using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Windows.Foundation;
using Windows.Media.Control;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.media` — currently-playing media (Spotify, browser <audio>,
/// MPV, Foobar, etc.) via the SystemMediaTransportControls service.
/// Event-driven, no polling. Mirrors `phone.media` on Android.
///
/// Emits on session list changes (app started / stopped playing) and
/// on the current session's playback/metadata callbacks. Self-disables
/// only if the SMTC service is unavailable, which is rare on supported
/// Windows builds.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DesktopMediaCollector : ICollector
{
    public string Surface => "desktop.media";

    public IAsyncEnumerable<CollectedEvent> StreamAsync(CancellationToken ct) =>
        ChannelCollectorScaffold.StreamAsync(emit =>
        {
            GlobalSystemMediaTransportControlsSessionManager? mgr;
            try
            {
                mgr = GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                emit(ClientObservations.CollectorDisabled(Surface, $"SMTC unavailable: {ex.Message}"));
                return ChannelCollectorScaffold.Teardown(() => { });
            }
            if (mgr is null)
            {
                emit(ClientObservations.CollectorDisabled(Surface, "SMTC RequestAsync returned null"));
                return ChannelCollectorScaffold.Teardown(() => { });
            }

            // Track subscriptions per-session so we don't leak handlers
            // when an app exits.
            var bound = new Dictionary<string, SessionBinding>();
            var bindingLock = new object();

            void TryBind(GlobalSystemMediaTransportControlsSession session)
            {
                var id = session.SourceAppUserModelId ?? "(unknown)";
                lock (bindingLock)
                {
                    if (bound.ContainsKey(id)) return;
                    var binding = new SessionBinding(id, session, emit);
                    bound[id] = binding;
                    binding.Attach();
                    binding.PushSnapshot("session_added");
                }
            }

            void RefreshAll()
            {
                var sessions = mgr.GetSessions() ?? new List<GlobalSystemMediaTransportControlsSession>();
                var live = new HashSet<string>();
                foreach (var s in sessions)
                {
                    var id = s.SourceAppUserModelId ?? "(unknown)";
                    live.Add(id);
                    TryBind(s);
                }
                lock (bindingLock)
                {
                    foreach (var key in bound.Keys.ToList())
                    {
                        if (!live.Contains(key))
                        {
                            bound[key].PushSnapshot("session_removed");
                            bound[key].Detach();
                            bound.Remove(key);
                        }
                    }
                }
            }

            // SessionsChanged is raised when apps start/stop registering.
            TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> sessionsChanged
                = (_, _) => RefreshAll();
            mgr.SessionsChanged += sessionsChanged;
            RefreshAll();

            return ChannelCollectorScaffold.Teardown(() =>
            {
                try { mgr.SessionsChanged -= sessionsChanged; } catch { }
                lock (bindingLock)
                {
                    foreach (var b in bound.Values) b.Detach();
                    bound.Clear();
                }
            });
        }, ct);

    private sealed class SessionBinding
    {
        private readonly string _id;
        private readonly GlobalSystemMediaTransportControlsSession _session;
        private readonly Action<CollectedEvent> _emit;

        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playback;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _media;

        public SessionBinding(string id, GlobalSystemMediaTransportControlsSession session, Action<CollectedEvent> emit)
        {
            _id = id; _session = session; _emit = emit;
        }

        public void Attach()
        {
            _playback = (_, _) => PushSnapshot("playback_changed");
            _media = (_, _) => PushSnapshot("metadata_changed");
            _session.PlaybackInfoChanged += _playback;
            _session.MediaPropertiesChanged += _media;
        }

        public void Detach()
        {
            try { if (_playback is not null) _session.PlaybackInfoChanged -= _playback; } catch { }
            try { if (_media is not null) _session.MediaPropertiesChanged -= _media; } catch { }
        }

        public void PushSnapshot(string trigger)
        {
            string? title = null, artist = null, album = null;
            try
            {
                var props = _session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                if (props is not null)
                {
                    title = props.Title;
                    artist = props.Artist;
                    album = props.AlbumTitle;
                }
            }
            catch { /* metadata may be unavailable mid-transition */ }

            var info = _session.GetPlaybackInfo();
            var state = info?.PlaybackStatus.ToString().ToLowerInvariant();

            var payload = JsonSerializer.Serialize(new
            {
                trigger,
                app = _id,
                state,
                title,
                artist,
                album,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            _emit(new CollectedEvent("desktop.media", payload, DateTimeOffset.UtcNow));
        }
    }
}
