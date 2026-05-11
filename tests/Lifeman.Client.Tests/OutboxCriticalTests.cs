using Lifeman.Client.Outbox;

namespace Lifeman.Client.Tests;

public sealed class OutboxCriticalTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteOutbox _outbox = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lifeman-outbox-critical-{Guid.NewGuid():N}.db");
        _outbox = new SqliteOutbox(_dbPath);
        await _outbox.InitAsync();
    }

    public async Task DisposeAsync()
    {
        await _outbox.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Trim_PreservesCriticalEntries()
    {
        // 30 noise rows + 2 critical rows. maxBytes=0 forces a trim
        // pass; 10% of 30 = 3 noise rows should be dropped, the critical
        // pair must survive.
        for (var i = 0; i < 30; i++)
            await _outbox.EnqueueAsync("noise", "{}", DateTimeOffset.UtcNow);
        var crit1 = await _outbox.EnqueueAsync("urgent.a", "{}", DateTimeOffset.UtcNow, isCritical: true);
        var crit2 = await _outbox.EnqueueAsync("urgent.b", "{}", DateTimeOffset.UtcNow, isCritical: true);

        var dropped = await _outbox.TrimAsync(maxBytes: 0);
        Assert.Equal(3, dropped);

        var remaining = await _outbox.PeekAsync(100);
        Assert.Contains(remaining, e => e.Id == crit1);
        Assert.Contains(remaining, e => e.Id == crit2);
        Assert.Equal(29, remaining.Count); // 30 - 3 noise + 2 critical = 29
        Assert.All(remaining.Where(e => e.Id == crit1 || e.Id == crit2), e => Assert.True(e.IsCritical));
    }

    [Fact]
    public async Task Trim_NoOpWhenAllCritical()
    {
        await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow, isCritical: true);
        await _outbox.EnqueueAsync("b", "{}", DateTimeOffset.UtcNow, isCritical: true);

        // Nothing droppable — trim should report zero rather than crashing
        // on a divide-by-zero or trying to delete the only rows.
        var dropped = await _outbox.TrimAsync(maxBytes: 0);
        Assert.Equal(0, dropped);
        Assert.Equal(2, await _outbox.CountAsync());
    }

    [Fact]
    public async Task Enqueue_DefaultsToNonCritical()
    {
        await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);
        var entry = (await _outbox.PeekAsync(1)).Single();
        Assert.False(entry.IsCritical);
    }
}
