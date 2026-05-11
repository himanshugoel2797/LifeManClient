using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Lifeman.Client.Collectors;

/// One permission to probe at periodic intervals. `Check` returns
/// whether the permission is currently granted plus an optional
/// human-readable reason ("ACCESS_FINE_LOCATION not granted",
/// "Notification listener disabled in Settings", …). The probe is
/// invoked from a threadpool thread; do not block for more than ~50ms.
public sealed record PermissionProbe(
    string Surface,
    string Permission,
    Func<(bool granted, string? reason)> Check);

/// `client.observation` emitter that periodically re-checks every
/// permission-gated collector's grant state. Emits a transition event
/// each time the grant state of a probe changes (granted→missing,
/// missing→granted) plus one boot-time event for every probe that is
/// missing on startup.
///
/// Implements CLIENT_DESIGN.md §Permission model: "The client periodically
/// self-audits each declared collector's permission state and emits an
/// observation event when a critical permission goes missing".
public sealed class PermissionAuditor : ICollector
{
    public string Surface => ClientObservations.Surface;

    private readonly IReadOnlyList<PermissionProbe> _probes;
    private readonly TimeSpan _interval;

    public PermissionAuditor(IReadOnlyList<PermissionProbe> probes, TimeSpan? interval = null)
    {
        _probes = probes;
        _interval = interval ?? TimeSpan.FromMinutes(10);
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_probes.Count == 0) yield break;

        var lastState = new Dictionary<string, bool>(_probes.Count);
        var firstPass = true;

        while (!ct.IsCancellationRequested)
        {
            foreach (var probe in _probes)
            {
                var (granted, reason) = SafeCheck(probe);
                lastState.TryGetValue(probe.Surface, out var prev);
                var hadPrev = lastState.ContainsKey(probe.Surface);
                lastState[probe.Surface] = granted;

                CollectedEvent? evt = null;
                if (firstPass && !granted)
                    evt = BuildEvent("permission_missing", probe, granted, reason);
                else if (hadPrev && prev && !granted)
                    evt = BuildEvent("permission_revoked", probe, granted, reason);
                else if (hadPrev && !prev && granted)
                    evt = BuildEvent("permission_granted", probe, granted, reason);

                if (evt is not null) yield return evt;
            }
            firstPass = false;

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private static (bool granted, string? reason) SafeCheck(PermissionProbe p)
    {
        try { return p.Check(); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static CollectedEvent BuildEvent(string kind, PermissionProbe probe, bool granted, string? reason)
    {
        var payload = JsonSerializer.Serialize(new
        {
            kind,
            surface = probe.Surface,
            permission = probe.Permission,
            granted,
            reason,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent(ClientObservations.Surface, payload, DateTimeOffset.UtcNow);
    }
}
