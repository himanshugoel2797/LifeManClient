using Lifeman.Client.Windows.Collectors;

namespace Lifeman.Client.Windows.Tests;

public sealed class DesktopIdleCollectorTests
{
    [Fact]
    public void GetIdleDuration_Returns_NonNegative()
    {
        // GetLastInputInfo is always callable on Windows; on a CI build
        // agent with no input it can return a large value, but never
        // negative.
        var idle = DesktopIdleCollector.GetIdleDuration();
        Assert.True(idle >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Emits_Initial_Bucket_Then_Honors_Cancellation()
    {
        // Force the "active" bucket to be tight so the very first poll
        // crosses a threshold; the collector still has to yield at
        // least one event when the bucket changes from null → something.
        var c = new DesktopIdleCollector(
            pollInterval: TimeSpan.FromMilliseconds(50),
            idleThreshold: TimeSpan.FromHours(24),
            longIdleThreshold: TimeSpan.FromHours(48));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var events = new List<string>();
        try
        {
            await foreach (var ev in c.StreamAsync(cts.Token))
            {
                events.Add(ev.PayloadJson);
                if (events.Count >= 1) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }

        Assert.Single(events);
        Assert.Contains("\"bucket\":\"active\"", events[0]);
    }
}
