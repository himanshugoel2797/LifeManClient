using Microsoft.Data.Sqlite;

namespace Lifeman.Client.Health;

/// SQLite-backed `IHealthStore`. Shares the on-disk file with the outbox
/// DB so a single file holds all client state — collectors don't need to
/// know about a second handle. The first call lazily ensures the `health`
/// table exists, so wiring order with `SqliteOutbox.InitAsync` doesn't
/// matter.
public sealed class SqliteHealthStore : IHealthStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _schemaReady;

    public SqliteHealthStore(string path) => _path = path;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={_path}");
        c.Open();
        return c;
    }

    private async ValueTask EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (Volatile.Read(ref _schemaReady) == 1) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS health (
                surface          TEXT PRIMARY KEY,
                last_success_at  TEXT,
                last_error_at    TEXT,
                last_error       TEXT,
                success_count    INTEGER NOT NULL DEFAULT 0,
                error_count      INTEGER NOT NULL DEFAULT 0
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _schemaReady, 1);
    }

    public async ValueTask RecordSuccessAsync(string surface, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await EnsureSchemaAsync(conn, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO health (surface, last_success_at, success_count)
                VALUES ($s, $t, 1)
                ON CONFLICT(surface) DO UPDATE SET
                    last_success_at = excluded.last_success_at,
                    success_count   = success_count + 1;
                """;
            cmd.Parameters.AddWithValue("$s", surface);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RecordErrorAsync(string surface, string error, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await EnsureSchemaAsync(conn, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO health (surface, last_error_at, last_error, error_count)
                VALUES ($s, $t, $e, 1)
                ON CONFLICT(surface) DO UPDATE SET
                    last_error_at = excluded.last_error_at,
                    last_error    = excluded.last_error,
                    error_count   = error_count + 1;
                """;
            cmd.Parameters.AddWithValue("$s", surface);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$e", error);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<HealthEntry>> SnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await EnsureSchemaAsync(conn, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT surface, last_success_at, last_error_at, last_error, success_count, error_count
                FROM health
                ORDER BY surface;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<HealthEntry>();
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new HealthEntry(
                    Surface: r.GetString(0),
                    LastSuccessAt: r.IsDBNull(1) ? null : DateTimeOffset.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    LastErrorAt: r.IsDBNull(2) ? null : DateTimeOffset.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    LastError: r.IsDBNull(3) ? null : r.GetString(3),
                    SuccessCount: r.GetInt64(4),
                    ErrorCount: r.GetInt64(5)));
            }
            return rows;
        }
        finally { _gate.Release(); }
    }
}
