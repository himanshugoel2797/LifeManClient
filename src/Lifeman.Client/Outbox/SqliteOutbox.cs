using Microsoft.Data.Sqlite;

namespace Lifeman.Client.Outbox;

public sealed class SqliteOutbox : IOutbox
{
    private readonly string _path;
    private SqliteConnection? _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteOutbox(string path)
    {
        _path = path;
    }

    public async ValueTask InitAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={_path}");
        await _conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = _conn.CreateCommand();
        // WAL keeps readers from blocking the uploader; the outbox is hit
        // from a single process but readers (debug tools, the collector
        // count tile) still benefit.
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS outbox (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                surface      TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                emitted_at   TEXT NOT NULL,
                attempts     INTEGER NOT NULL DEFAULT 0,
                last_error   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_outbox_emitted ON outbox(emitted_at);
            CREATE TABLE IF NOT EXISTS received (
                output_id    TEXT PRIMARY KEY,
                received_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_received_at ON received(received_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private SqliteConnection Conn => _conn ?? throw new InvalidOperationException("Outbox not initialised — call InitAsync.");

    public async ValueTask<long> EnqueueAsync(string surface, string payloadJson, DateTimeOffset emittedAt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO outbox (surface, payload_json, emitted_at)
                VALUES ($s, $p, $e);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$s", surface);
            cmd.Parameters.AddWithValue("$p", payloadJson);
            cmd.Parameters.AddWithValue("$e", emittedAt.ToString("O"));
            var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<OutboxEntry>> PeekAsync(int max, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, surface, payload_json, emitted_at, attempts, last_error
                FROM outbox
                ORDER BY id ASC
                LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", max);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var results = new List<OutboxEntry>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(new OutboxEntry(
                    Id: reader.GetInt64(0),
                    Surface: reader.GetString(1),
                    PayloadJson: reader.GetString(2),
                    EmittedAt: DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Attempts: reader.GetInt32(4),
                    LastError: reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask AckAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await Conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = Conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM outbox WHERE id = $id;";
            var p = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                p.Value = id;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FailAsync(IReadOnlyCollection<long> ids, string error, bool permanent, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var tx = (SqliteTransaction)await Conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var cmd = Conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = permanent
                ? "DELETE FROM outbox WHERE id = $id;"
                : "UPDATE outbox SET attempts = attempts + 1, last_error = $err WHERE id = $id;";
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            if (!permanent)
            {
                cmd.Parameters.AddWithValue("$err", error);
            }
            foreach (var id in ids)
            {
                pId.Value = id;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<long> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox;";
            return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> TrimAsync(long maxBytes, CancellationToken ct = default)
    {
        var fi = new FileInfo(_path);
        if (!fi.Exists || fi.Length <= maxBytes) return 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop oldest 10% by row count, then VACUUM. Coarse but cheap;
            // we don't want to do a binary search over the file size on
            // every overflow.
            await using var countCmd = Conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM outbox;";
            var total = (long)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            var drop = (int)Math.Max(1, total / 10);

            await using var delCmd = Conn.CreateCommand();
            delCmd.CommandText = "DELETE FROM outbox WHERE id IN (SELECT id FROM outbox ORDER BY id ASC LIMIT $n);";
            delCmd.Parameters.AddWithValue("$n", drop);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var vac = Conn.CreateCommand();
            vac.CommandText = "VACUUM;";
            await vac.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return drop;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> TryMarkReceivedAsync(string outputId, DateTimeOffset receivedAt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = Conn.CreateCommand();
            // INSERT OR IGNORE — `changes()` is 1 for a fresh insert, 0 if the
            // PK already existed. That's our "have we surfaced this before?" answer.
            cmd.CommandText = """
                INSERT OR IGNORE INTO received (output_id, received_at) VALUES ($id, $at);
                SELECT changes();
                """;
            cmd.Parameters.AddWithValue("$id", outputId);
            cmd.Parameters.AddWithValue("$at", receivedAt.ToString("O"));
            var changed = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
            return changed == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<int> TrimReceivedAsync(TimeSpan retain, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - retain;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText = "DELETE FROM received WHERE received_at < $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
        }
        _gate.Dispose();
    }
}
