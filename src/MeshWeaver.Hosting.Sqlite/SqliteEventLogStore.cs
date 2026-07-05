using System.Reactive;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Data.Sqlite;

namespace MeshWeaver.Hosting.Sqlite;

/// <summary>
/// SQLite <see cref="IEventLogStore"/> — the durable event-log outbox for the embedded / offline
/// backend (MAUI and other single-file deployments). Mirrors <c>SqliteStorageAdapter</c>: one held
/// <see cref="SqliteConnection"/>, all access serialised through a lock and run off the hub scheduler
/// on the <see cref="IIoPool"/> (SQLite leaves are sync-blocking). Append is idempotent by
/// <c>(path, kind, version)</c> via UPSERT. SQLite has no schemas, so the tables are
/// <c>event_log</c> / <c>event_cursor</c> (created on construction).
/// </summary>
public sealed class SqliteEventLogStore : IEventLogStore, IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private readonly IIoPool _ioPool;
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Opens the connection (kept open — an in-memory DB lives only while a connection is)
    /// and creates the event-log tables.</summary>
    public SqliteEventLogStore(string connectionString, IIoPool? ioPool = null)
    {
        _ioPool = ioPool ?? IoPool.Unbounded;
        var csb = new SqliteConnectionStringBuilder(connectionString) { Pooling = false };
        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS event_log (
                seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                namespace   TEXT NOT NULL DEFAULT '',
                path        TEXT NOT NULL,
                node_type   TEXT,
                kind        TEXT NOT NULL,
                version     INTEGER NOT NULL DEFAULT 0,
                payload     TEXT,
                UNIQUE (path, kind, version)
            );
            CREATE TABLE IF NOT EXISTS event_cursor (
                consumer_id TEXT PRIMARY KEY,
                last_seq    INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IObservable<long> Append(MeshChangeEvent change) => _ioPool.InvokeBlocking(_ =>
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO event_log (occurred_at, namespace, path, node_type, kind, version, payload)
                VALUES ($occurred, $ns, $path, $nt, $kind, $ver, $payload)
                ON CONFLICT (path, kind, version) DO UPDATE SET occurred_at = occurred_at
                RETURNING seq;
                """;
            cmd.Parameters.AddWithValue("$occurred", change.Timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("$ns", change.Namespace ?? "");
            cmd.Parameters.AddWithValue("$path", change.Path);
            cmd.Parameters.AddWithValue("$nt", (object?)change.NodeType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", change.Kind.ToString());
            cmd.Parameters.AddWithValue("$ver", change.Version);
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(change, JsonOptions));
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<EventLogEntry>> ReadFrom(long afterSeq, int limit = 500) =>
        _ioPool.InvokeBlocking(_ =>
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT seq, payload FROM event_log WHERE seq > $after ORDER BY seq LIMIT $limit;";
                cmd.Parameters.AddWithValue("$after", afterSeq);
                cmd.Parameters.AddWithValue("$limit", limit);
                var list = new List<EventLogEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var evt = JsonSerializer.Deserialize<MeshChangeEvent>(reader.GetString(1), JsonOptions);
                    if (evt is not null)
                        list.Add(new EventLogEntry(reader.GetInt64(0), evt));
                }
                return (IReadOnlyList<EventLogEntry>)list;
            }
        });

    /// <inheritdoc />
    public IObservable<long> MaxSeq() => _ioPool.InvokeBlocking(_ =>
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM event_log;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    });

    /// <inheritdoc />
    public IObservable<long> GetCursor(string consumerId) => _ioPool.InvokeBlocking(_ =>
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT last_seq FROM event_cursor WHERE consumer_id = $id;";
            cmd.Parameters.AddWithValue("$id", consumerId);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0L : Convert.ToInt64(v);
        }
    });

    /// <inheritdoc />
    public IObservable<Unit> SetCursor(string consumerId, long seq) => _ioPool.InvokeBlocking(_ =>
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO event_cursor (consumer_id, last_seq) VALUES ($id, $seq)
                ON CONFLICT (consumer_id) DO UPDATE SET last_seq = MAX(event_cursor.last_seq, excluded.last_seq);
                """;
            cmd.Parameters.AddWithValue("$id", consumerId);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.ExecuteNonQuery();
            return Unit.Default;
        }
    });

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}
