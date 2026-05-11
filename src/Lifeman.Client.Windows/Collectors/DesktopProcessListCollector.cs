using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.process_list` — periodic snapshot of the user's running
/// processes (CLIENT_DESIGN.md: "Process list (WMI), 5-min poll, low
/// volume"). We use `Process.GetProcesses()` rather than WMI: WMI for a
/// process list costs ~10× the CPU and adds no signal we'd actually use
/// at this resolution. The kernel's input router doesn't need exe paths,
/// command lines, or owner SIDs to spot "user opened a game" — process
/// names are enough.
[SupportedOSPlatform("windows")]
public sealed class DesktopProcessListCollector : ICollector
{
    public string Surface => "desktop.process_list";

    private readonly TimeSpan _interval;

    public DesktopProcessListCollector(TimeSpan? interval = null)
        => _interval = interval ?? TimeSpan.FromMinutes(5);

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CollectedEvent? snap = null;
            try { snap = Snapshot(); }
            catch (Exception)
            {
                // Process enumeration races against process-exit; swallow
                // and retry next tick rather than tearing down the collector.
            }
            if (snap is not null) yield return snap;

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private static CollectedEvent Snapshot()
    {
        // GroupBy + Count collapses the half-dozen `chrome.exe` /
        // `svchost.exe` instances into one row. Lighter payload, easier
        // for the kernel-side handler to summarize. PID list is dropped
        // entirely — the ID churns and tells us nothing useful.
        var procs = Process.GetProcesses();
        try
        {
            var rows = procs
                .GroupBy(p => SafeName(p), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderByDescending(g => g.Count())
                .Select(g => new { name = g.Key, count = g.Count() })
                .ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                total = procs.Length,
                unique = rows.Length,
                processes = rows,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            return new CollectedEvent("desktop.process_list", payload, DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch { return string.Empty; }
    }
}
