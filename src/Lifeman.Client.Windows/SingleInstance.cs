using System.Security.Principal;

namespace Lifeman.Client.Windows;

/// Process-wide mutex so a user can't accidentally run two agent copies
/// (e.g. autostart + manual launch). Scoped per Windows user — admins
/// running concurrent sessions on a shared box get their own slot.
public sealed class SingleInstance : IDisposable
{
    // Named Semaphore (not Mutex): mutexes are thread-affinitised and
    // reentrant on the owning thread, so two acquires from the same
    // thread (e.g. parent + child within one process) both succeed,
    // which defeats single-instance semantics inside tests and inside
    // any callers that share a startup thread.
    private readonly Semaphore? _sem;
    public bool Acquired { get; }

    private SingleInstance(Semaphore? sem, bool acquired)
    {
        _sem = sem;
        Acquired = acquired;
    }

    public static SingleInstance TryAcquire()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var name = $"Local\\lifeman-client-{sid}";
        var sem = new Semaphore(initialCount: 1, maximumCount: 1, name);
        var acquired = sem.WaitOne(TimeSpan.Zero);
        if (!acquired) { sem.Dispose(); return new SingleInstance(null, false); }
        return new SingleInstance(sem, true);
    }

    public void Dispose()
    {
        if (_sem is null) return;
        try { if (Acquired) _sem.Release(); } catch { }
        _sem.Dispose();
    }
}
