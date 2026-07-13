using System.Data;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Durable Snowflake <see cref="IEventLogStore"/> — the app-level outbox persisted in the
/// <see cref="SnowflakeStorageOptions.EventsSchema"/> schema (tables created by the schema
/// initializer), mirroring <c>PostgreSqlEventLogStore</c> member-for-member so the event log
/// survives restarts and is queryable/replayable across silos. Append is idempotent by
/// <c>(path, kind, version)</c>: Snowflake enforces no unique constraints, so the store uses
/// <c>INSERT … SELECT … WHERE NOT EXISTS</c> followed by a read-back of the (canonical, minimum)
/// assigned seq instead of PG's <c>ON CONFLICT</c>. Every appended row is additionally stamped
/// with this silo's <see cref="SnowflakeOriginId"/> (column <c>origin_id</c>) so the
/// <see cref="SnowflakeChangeFeedPoller"/> can filter the silo's own writes out of the polled
/// live feed. All I/O runs on the Snowflake I/O pools (never a bare <c>FromAsync</c>).
/// </summary>
public sealed class SnowflakeEventLogStore : IEventLogStore
{
    private const string PoolAdapter = "eventlog";
    private const int DefaultPageSize = 500;

    private readonly SnowflakeConnectionSource _source;
    private readonly SnowflakeOriginId _origin;
    // Writes on the cap-1 sf:{adapter} pool (serialises through a single logical connection, the
    // adapter idiom); reads on the bounded sf-read:{adapter} pool — the same split the PG store
    // uses so a read fan-out can never starve writes.
    private readonly IIoPool _writePool;
    private readonly IIoPool _readPool;
    // Qualified, double-quoted-lowercase table references, precomputed from the configured schema.
    private readonly string _eventLogTable;
    private readonly string _cursorTable;
    // Immutable serializer config initialized once — a constant, not a cache.
    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>
    /// Creates the store over the shared connection source, this silo's origin identity,
    /// the storage options (events-schema name) and the Snowflake read/write I/O pools.
    /// </summary>
    /// <param name="source">The one place that opens Snowflake connections.</param>
    /// <param name="origin">Per-silo identity stamped into <c>origin_id</c> on every appended row.</param>
    /// <param name="options">Storage options; <see cref="SnowflakeStorageOptions.EventsSchema"/> locates the log tables.</param>
    /// <param name="ioPoolRegistry">Mesh-scoped pool registry; when null (bare unit tests) the unbounded fallback pool is used.</param>
    public SnowflakeEventLogStore(
        SnowflakeConnectionSource source,
        SnowflakeOriginId origin,
        SnowflakeStorageOptions options,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _source = source;
        _origin = origin;
        _writePool = ioPoolRegistry?.Get(IoPoolNames.SnowflakeAdapterPrefix + PoolAdapter) ?? IoPool.Unbounded;
        _readPool = ioPoolRegistry?.Get(IoPoolNames.SnowflakeReadAdapterPrefix + PoolAdapter) ?? IoPool.Unbounded;
        _eventLogTable = SnowflakeIdentifiers.Qualify(options.EventsSchema, "event_log");
        _cursorTable = SnowflakeIdentifiers.Qualify(options.EventsSchema, "action_cursor");
    }

    /// <inheritdoc />
    public IObservable<long> Append(MeshChangeEvent change) => _writePool.Invoke(async ct =>
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        // Idempotent insert: Snowflake has no unique constraints / ON CONFLICT, so the guard is
        // INSERT … SELECT … WHERE NOT EXISTS on the (path, kind, version) key. The dummy
        // one-row FROM keeps the WHERE clause portable across the emulator's transpiler.
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO {_eventLogTable}
                    ("occurred_at", "namespace", "path", "node_type", "kind", "version", "payload", "origin_id")
                SELECT :occurred_at, :namespace, :path, :node_type, :kind, :version, :payload, :origin_id
                FROM (SELECT 1 AS "x")
                WHERE NOT EXISTS (
                    SELECT 1 FROM {_eventLogTable}
                    WHERE "path" = :path AND "kind" = :kind AND "version" = :version)
                """;
            SnowflakeConnectionSource.AddParam(insert, "occurred_at", change.Timestamp.UtcDateTime, DbType.DateTime);
            SnowflakeConnectionSource.AddParam(insert, "namespace", change.Namespace ?? "", DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "path", change.Path, DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "node_type", change.NodeType, DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "kind", change.Kind.ToString(), DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "version", change.Version, DbType.Int64);
            SnowflakeConnectionSource.AddParam(insert, "payload", JsonSerializer.Serialize(change, JsonOptions), DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "origin_id", _origin.Value, DbType.String);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Read the assigned seq back — existing on a duplicate, new otherwise. MIN makes the
        // answer canonical even if a cross-silo race slipped two rows past the NOT EXISTS guard
        // (both silos then agree on the same seq for the same logical event).
        await using var select = connection.CreateCommand();
        select.CommandText = $"""
            SELECT MIN("seq") FROM {_eventLogTable}
            WHERE "path" = :path AND "kind" = :kind AND "version" = :version
            """;
        SnowflakeConnectionSource.AddParam(select, "path", change.Path, DbType.String);
        SnowflakeConnectionSource.AddParam(select, "kind", change.Kind.ToString(), DbType.String);
        SnowflakeConnectionSource.AddParam(select, "version", change.Version, DbType.Int64);
        var seq = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(seq);
    });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<EventLogEntry>> ReadFrom(long afterSeq, int limit = DefaultPageSize) =>
        ReadFromWithOrigin(afterSeq, limit)
            .Select(rows => (IReadOnlyList<EventLogEntry>)rows.Select(r => r.Entry).ToList());

    /// <summary>
    /// Same page read as <see cref="ReadFrom"/> but also selecting each row's <c>origin_id</c>
    /// stamp. The <see cref="SnowflakeChangeFeedPoller"/> uses this to drop rows this silo
    /// appended itself (already published in-process) while forwarding foreign silos' events.
    /// A null origin (rows written by other/older writers) is treated as foreign by the poller.
    /// </summary>
    /// <param name="afterSeq">Only rows with <c>seq</c> strictly greater than this are returned, in seq order.</param>
    /// <param name="limit">Maximum number of rows in the page.</param>
    internal IObservable<IReadOnlyList<(EventLogEntry Entry, string? OriginId)>> ReadFromWithOrigin(
        long afterSeq, int limit) =>
        _readPool.Invoke(async ct =>
        {
            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT "seq", "payload", "origin_id" FROM {_eventLogTable}
                WHERE "seq" > :after_seq ORDER BY "seq" LIMIT :limit
                """;
            SnowflakeConnectionSource.AddParam(cmd, "after_seq", afterSeq, DbType.Int64);
            SnowflakeConnectionSource.AddParam(cmd, "limit", limit, DbType.Int32);
            var list = new List<(EventLogEntry Entry, string? OriginId)>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var seq = reader.GetInt64(0);
                var json = reader.GetString(1);
                var originId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var evt = JsonSerializer.Deserialize<MeshChangeEvent>(json, JsonOptions);
                if (evt is not null)
                    list.Add((new EventLogEntry(seq, evt), originId));
            }
            return (IReadOnlyList<(EventLogEntry Entry, string? OriginId)>)list;
        });

    /// <inheritdoc />
    public IObservable<long> MaxSeq() => _readPool.Invoke(async ct =>
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""SELECT COALESCE(MAX("seq"), 0) FROM {_eventLogTable}""";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    });

    /// <inheritdoc />
    public IObservable<long> GetCursor(string consumerId) => _readPool.Invoke(async ct =>
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""SELECT "last_seq" FROM {_cursorTable} WHERE "consumer_id" = :consumer_id""";
        SnowflakeConnectionSource.AddParam(cmd, "consumer_id", consumerId, DbType.String);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? 0L : Convert.ToInt64(v);
    });

    /// <inheritdoc />
    public IObservable<Unit> SetCursor(string consumerId, long seq) => _writePool.Invoke(async ct =>
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        // No ON CONFLICT (and MERGE may be unsupported on the emulator): seed-if-missing, then a
        // GREATEST-guarded update keeps the cursor monotone. Both statements run inside one cap-1
        // write-pool slot, so within this silo the pair is effectively atomic.
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                INSERT INTO {_cursorTable} ("consumer_id", "last_seq")
                SELECT :consumer_id, :seq
                FROM (SELECT 1 AS "x")
                WHERE NOT EXISTS (SELECT 1 FROM {_cursorTable} WHERE "consumer_id" = :consumer_id)
                """;
            SnowflakeConnectionSource.AddParam(insert, "consumer_id", consumerId, DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "seq", seq, DbType.Int64);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = $"""
            UPDATE {_cursorTable} SET "last_seq" = GREATEST("last_seq", :seq)
            WHERE "consumer_id" = :consumer_id
            """;
        SnowflakeConnectionSource.AddParam(update, "seq", seq, DbType.Int64);
        SnowflakeConnectionSource.AddParam(update, "consumer_id", consumerId, DbType.String);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return Unit.Default;
    });
}
