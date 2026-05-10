using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.active_window` — polls GetForegroundWindow and emits when the
/// foreground app or window title changes. Polling (vs SetWinEventHook)
/// because a console-host process has no message loop to dispatch the
/// hook callback on. 2s cadence keeps wakeups cheap and is well below
/// the human-perceptible "I switched apps" threshold.
[SupportedOSPlatform("windows")]
public sealed class DesktopActiveWindowCollector : ICollector
{
    public string Surface => "desktop.active_window";

    private readonly TimeSpan _interval;

    public DesktopActiveWindowCollector(TimeSpan? interval = null)
        => _interval = interval ?? TimeSpan.FromSeconds(2);

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? lastProcess = null;
        string? lastTitle = null;
        IntPtr lastHwnd = IntPtr.Zero;

        while (!ct.IsCancellationRequested)
        {
            var snap = TrySnapshot();
            if (snap is not null
                && (snap.Value.Hwnd != lastHwnd
                    || snap.Value.Process != lastProcess
                    || snap.Value.Title != lastTitle))
            {
                lastHwnd = snap.Value.Hwnd;
                lastProcess = snap.Value.Process;
                lastTitle = snap.Value.Title;
                yield return Emit(snap.Value);
            }
            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private static CollectedEvent Emit(in Snapshot s)
    {
        var payload = JsonSerializer.Serialize(new
        {
            process = s.Process,
            title = s.Title,
            pid = s.Pid,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        });
        return new CollectedEvent("desktop.active_window", payload, DateTimeOffset.UtcNow);
    }

    private readonly record struct Snapshot(IntPtr Hwnd, int Pid, string? Process, string? Title);

    private static Snapshot? TrySnapshot()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        string? proc = null;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            proc = p.ProcessName;
        }
        catch { /* process exited between calls; leave null */ }

        var len = Native.GetWindowTextLength(hwnd);
        string? title = null;
        if (len > 0)
        {
            var sb = new StringBuilder(len + 1);
            if (Native.GetWindowText(hwnd, sb, sb.Capacity) > 0) title = sb.ToString();
        }
        return new Snapshot(hwnd, (int)pid, proc, title);
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
