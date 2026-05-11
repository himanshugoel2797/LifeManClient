using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Lifeman.Client.Collectors;

/// Shared plumbing for collectors whose OS callbacks produce events
/// asynchronously: open a channel, hand the writer to the platform
/// `attach` callback, drain it as an `IAsyncEnumerable`, and unregister
/// on cancellation. Replaces the ~20-line scaffold each collector used
/// to repeat verbatim.
public static class ChannelCollectorScaffold
{
    /// Drive an async stream from an event-callback source. `attach` is
    /// invoked once with an `emit` callback the platform code uses to
    /// push events; it returns a disposable that unregisters the OS
    /// callback on shutdown. Cancellation completes the channel so the
    /// consumer's `await foreach` exits cleanly.
    public static async IAsyncEnumerable<CollectedEvent> StreamAsync(
        Func<Action<CollectedEvent>, IDisposable> attach,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<CollectedEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // The attach callback may synchronously emit a startup snapshot
        // (sticky broadcast replay etc.) — that's a normal pattern and
        // arrives before the channel reader starts iterating.
        using var attached = attach(ev => channel.Writer.TryWrite(ev));
        using var reg = ct.Register(() => channel.Writer.TryComplete());

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    /// Build an IDisposable from a teardown action. Used by `attach`
    /// callbacks to encapsulate "undo the OS registration" without each
    /// collector defining a one-off IDisposable class.
    public static IDisposable Teardown(Action onDispose) => new TeardownDisposable(onDispose);

    private sealed class TeardownDisposable : IDisposable
    {
        private Action? _onDispose;
        public TeardownDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            var fn = Interlocked.Exchange(ref _onDispose, null);
            if (fn is not null) try { fn(); } catch { /* teardown is best-effort */ }
        }
    }
}
