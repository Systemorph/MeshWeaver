using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Npgsql;
using NpgsqlTypes;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Durable Postgres <see cref="IEventLogStore"/> — the app-level outbox persisted in the
/// <c>events</c> schema (created by the <c>V40</c> migration), so the event log survives restarts and
/// is queryable/replayable across silos. Append is idempotent by <c>(path, kind, version)</c> via
/// <c>ON CONFLICT</c>. All I/O runs on the Postgres I/O pools (never a bare <c>FromAsync</c>).
/// </summary>
public sealed class PostgreSqlEventLogStore : IEventLogStore
{
    private const string PoolAdapter = "eventlog";
    private readonly NpgsqlDataSource _dataSource;
    // Writes on the cap-1 pg:{adapter} pool (serialises through a single connection, the adapter idiom);
    // reads on the cap-16 pg-read:{adapter} pool. Both are bounded WELL under Npgsql's pool — unlike the
    // FileSystem pool (cap 256), which could open enough concurrent commands to exhaust the connection pool.
    private readonly IIoPool _writePool;
    private readonly IIoPool _readPool;
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Creates the store over the shared data source + the Postgres read/write I/O pools.</summary>
    public PostgreSqlEventLogStore(NpgsqlDataSource dataSource, IoPoolRegistry? ioPoolRegistry = null)
    {
        _dataSource = dataSource;
        _writePool = ioPoolRegistry?.Get(IoPoolNames.PostgresAdapterPrefix + PoolAdapter) ?? IoPool.Unbounded;
        _readPool = ioPoolRegistry?.Get(IoPoolNames.PostgresReadAdapterPrefix + PoolAdapter) ?? IoPool.Unbounded;
    }

    /// <inheritdoc />
    public IObservable<long> Append(MeshChangeEvent change) => _writePool.Invoke(async ct =>
    {
        // ON CONFLICT ... DO UPDATE (a no-op touch) so RETURNING always yields the seq — existing on
        // a duplicate, new otherwise — without a second round-trip.
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO events.event_log (occurred_at, namespace, path, node_type, kind, version, payload)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (path, kind, version)
            DO UPDATE SET occurred_at = events.event_log.occurred_at
            RETURNING seq;
            """);
        cmd.Parameters.AddWithValue(change.Timestamp);
        cmd.Parameters.AddWithValue(change.Namespace ?? "");
        cmd.Parameters.AddWithValue(change.Path);
        cmd.Parameters.AddWithValue((object?)change.NodeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(change.Kind.ToString());
        cmd.Parameters.AddWithValue(change.Version);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(change, JsonOptions) });
        var seq = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(seq);
    });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<EventLogEntry>> ReadFrom(long afterSeq, int limit = 500) =>
        _readPool.Invoke(async ct =>
        {
            await using var cmd = _dataSource.CreateCommand("""
                SELECT seq, payload FROM events.event_log
                WHERE seq > $1 ORDER BY seq LIMIT $2;
                """);
            cmd.Parameters.AddWithValue(afterSeq);
            cmd.Parameters.AddWithValue(limit);
            var list = new List<EventLogEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var seq = reader.GetInt64(0);
                var json = reader.GetString(1);
                var evt = JsonSerializer.Deserialize<MeshChangeEvent>(json, JsonOptions);
                if (evt is not null)
                    list.Add(new EventLogEntry(seq, evt));
            }
            return (IReadOnlyList<EventLogEntry>)list;
        });

    /// <inheritdoc />
    public IObservable<long> MaxSeq() => _readPool.Invoke(async ct =>
    {
        await using var cmd = _dataSource.CreateCommand("SELECT COALESCE(MAX(seq), 0) FROM events.event_log;");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    });

    /// <inheritdoc />
    public IObservable<long> GetCursor(string consumerId) => _readPool.Invoke(async ct =>
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT last_seq FROM events.action_cursor WHERE consumer_id = $1;");
        cmd.Parameters.AddWithValue(consumerId);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    });

    /// <inheritdoc />
    public IObservable<Unit> SetCursor(string consumerId, long seq) => _writePool.Invoke(async ct =>
    {
        await using var cmd = _dataSource.CreateCommand("""
            INSERT INTO events.action_cursor (consumer_id, last_seq) VALUES ($1, $2)
            ON CONFLICT (consumer_id) DO UPDATE SET last_seq = GREATEST(events.action_cursor.last_seq, EXCLUDED.last_seq);
            """);
        cmd.Parameters.AddWithValue(consumerId);
        cmd.Parameters.AddWithValue(seq);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return Unit.Default;
    });
}
