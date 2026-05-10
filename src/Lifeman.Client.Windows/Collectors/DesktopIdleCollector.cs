using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.idle` — emits when the user transitions between activity
/// buckets (active → idle → long-idle and back). Uses GetLastInputInfo,
/// which tracks the system-wide last input timestamp without requiring
/// any hook installation.
///
/// Buckets:
///   active     : last input ≤ 60s ago
///   idle       : 60s..5m
///   long_idle  : > 5m
///
/// Polling at 30s keeps wake-ups cheap; the bucket granularity makes
/// missed sub-bucket transitions invisible anyway.
[SupportedOSPlatform("windows")]
public sealed class DesktopIdleCollector : ICollector
{
    public string Surface => "desktop.idle";

    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _longIdleThreshold;

    public DesktopIdleCollector(
        TimeSpan? pollInterval = null,
        TimeSpan? idleThreshold = null,
        TimeSpan? longIdleThreshold = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(1);
        _longIdleThreshold = longIdleThreshold ?? TimeSpan.FromMinutes(5);
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? lastBucket = null;
        while (!ct.IsCancellationRequested)
        {
            var idleFor = GetIdleDuration();
            var bucket = idleFor >= _longIdleThreshold ? "long_idle"
                       : idleFor >= _idleThreshold ? "idle"
                       : "active";
            if (bucket != lastBucket)
            {
                lastBucket = bucket;
                var payload = JsonSerializer.Serialize(new
                {
                    bucket,
                    idle_seconds = (long)idleFor.TotalSeconds,
                    timestamp = DateTimeOffset.UtcNow.ToString("O"),
                });
                yield return new CollectedEvent("desktop.idle", payload, DateTimeOffset.UtcNow);
            }
            try { await Task.Delay(_pollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    public static TimeSpan GetIdleDuration()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // GetTickCount wraps every ~49.7 days; subtraction in unsigned space handles wrap.
        var nowTicks = Native.GetTickCount();
        var elapsedMs = unchecked(nowTicks - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetTickCount();
    }
}
