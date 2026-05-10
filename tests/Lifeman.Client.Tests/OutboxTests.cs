using Lifeman.Client.Outbox;

namespace Lifeman.Client.Tests;

public sealed class OutboxTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteOutbox _outbox = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lifeman-outbox-{Guid.NewGuid():N}.db");
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
    public async Task Enqueue_Peek_Ack_RoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var id = await _outbox.EnqueueAsync("phone.battery", "{\"level\":0.8}", now);
        Assert.True(id > 0);

        var batch = await _outbox.PeekAsync(10);
        var entry = Assert.Single(batch);
        Assert.Equal("phone.battery", entry.Surface);
        Assert.Equal("{\"level\":0.8}", entry.PayloadJson);
        Assert.Equal(0, entry.Attempts);

        await _outbox.AckAsync(new[] { entry.Id });
        Assert.Equal(0, await _outbox.CountAsync());
    }

    [Fact]
    public async Task Peek_Returns_OldestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _outbox.EnqueueAsync("a", "{}", now);
        await _outbox.EnqueueAsync("b", "{}", now.AddSeconds(1));
        await _outbox.EnqueueAsync("c", "{}", now.AddSeconds(2));

        var batch = await _outbox.PeekAsync(10);
        Assert.Equal(new[] { "a", "b", "c" }, batch.Select(e => e.Surface).ToArray());
    }

    [Fact]
    public async Task Fail_NonPermanent_IncrementsAttempts()
    {
        var id = await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);
        await _outbox.FailAsync(new[] { id }, "boom", permanent: false);
        var batch = await _outbox.PeekAsync(10);
        var entry = Assert.Single(batch);
        Assert.Equal(1, entry.Attempts);
        Assert.Equal("boom", entry.LastError);
    }

    [Fact]
    public async Task Fail_Permanent_Deletes()
    {
        var id = await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);
        await _outbox.FailAsync(new[] { id }, "poison", permanent: true);
        Assert.Equal(0, await _outbox.CountAsync());
    }

    [Fact]
    public async Task Peek_LimitsByMax()
    {
        for (var i = 0; i < 5; i++)
            await _outbox.EnqueueAsync("a", "{}", DateTimeOffset.UtcNow);
        var batch = await _outbox.PeekAsync(3);
        Assert.Equal(3, batch.Count);
    }
}
