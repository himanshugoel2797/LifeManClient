using Lifeman.Client.Health;
using Lifeman.Client.Outbox;

namespace Lifeman.Client.Tests;

public sealed class HealthStoreTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private SqliteOutbox _outbox = null!;
    private SqliteHealthStore _health = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lifeman-health-{Guid.NewGuid():N}.db");
        _outbox = new SqliteOutbox(_dbPath);
        await _outbox.InitAsync(); // creates the `health` table
        _health = new SqliteHealthStore(_dbPath);
    }

    public async Task DisposeAsync()
    {
        await _outbox.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task RecordSuccess_AccumulatesCount()
    {
        await _health.RecordSuccessAsync("phone.battery");
        await _health.RecordSuccessAsync("phone.battery");
        await _health.RecordSuccessAsync("phone.battery");

        var snap = await _health.SnapshotAsync();
        var entry = Assert.Single(snap);
        Assert.Equal("phone.battery", entry.Surface);
        Assert.Equal(3, entry.SuccessCount);
        Assert.Equal(0, entry.ErrorCount);
        Assert.NotNull(entry.LastSuccessAt);
        Assert.Null(entry.LastErrorAt);
    }

    [Fact]
    public async Task RecordError_DoesNotResetSuccessCount()
    {
        await _health.RecordSuccessAsync("phone.foreground_app");
        await _health.RecordSuccessAsync("phone.foreground_app");
        await _health.RecordErrorAsync("phone.foreground_app", "permission revoked");

        var entry = (await _health.SnapshotAsync()).Single();
        Assert.Equal(2, entry.SuccessCount);
        Assert.Equal(1, entry.ErrorCount);
        Assert.Equal("permission revoked", entry.LastError);
        Assert.NotNull(entry.LastSuccessAt);
        Assert.NotNull(entry.LastErrorAt);
    }

    [Fact]
    public async Task Snapshot_IsAlphabetical()
    {
        await _health.RecordSuccessAsync("zeta");
        await _health.RecordSuccessAsync("alpha");
        await _health.RecordSuccessAsync("mu");

        var snap = await _health.SnapshotAsync();
        Assert.Equal(new[] { "alpha", "mu", "zeta" }, snap.Select(e => e.Surface).ToArray());
    }
}
