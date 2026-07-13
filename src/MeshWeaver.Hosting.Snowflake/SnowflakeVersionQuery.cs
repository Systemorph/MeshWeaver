using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Snowflake implementation of <see cref="IVersionQuery"/> — the member-for-member port of
/// <c>PostgreSqlVersionQuery</c>. Queries the <c>mesh_node_history</c> table (schema-qualified
/// when a schema is supplied, otherwise resolved against the connection's current schema).
/// All public methods return <see cref="IObservable{T}"/> — see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>.
///
/// <para><b>Dialect notes</b> (vs the PG original): identifiers are double-quoted lowercase via
/// <see cref="SnowflakeIdentifiers"/>; positional <c>$N</c> parameters become named <c>:pN</c>;
/// the <c>$12::jsonb</c> content cast becomes <c>PARSE_JSON(:p12)</c> (VARIANT);
/// <c>ON CONFLICT … DO NOTHING</c> becomes <c>INSERT … SELECT … WHERE NOT EXISTS</c> (Snowflake
/// enforces no unique constraints); and <c>path</c> — a GENERATED column in PG — is a real
/// NOT NULL column here, computed in SQL with exactly the PG generation semantics
/// (<c>CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END</c>, see
/// <see cref="SnowflakeSchemaInitializer"/>).</para>
/// </summary>
public class SnowflakeVersionQuery : IVersionQuery
{
    private readonly SnowflakeConnectionSource _source;
    private readonly string _historyTable;
    // Every DB round-trip runs inside the Snowflake I/O pool (Invoke), never a bare FromAsync.
    private readonly IIoPool _ioPool;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes the version query over the <c>mesh_node_history</c> table.
    /// </summary>
    /// <param name="source">The one place that opens Snowflake connections (the role <c>NpgsqlDataSource</c> plays for PG).</param>
    /// <param name="schema">Optional schema name; when set, the history table is schema-qualified, otherwise resolved against the connection's current schema.</param>
    /// <param name="ioPool">
    /// Optional I/O pool; every DB round-trip runs inside it. Callers pass the adapter's
    /// <c>sf:{adapter}</c> pool (<see cref="IoPoolNames.SnowflakeAdapterPrefix"/>) — mirroring how
    /// the PG version query receives its <c>pg:{adapter}</c> pool from its construction site.
    /// Falls back to the unbounded pool outside DI.
    /// </param>
    /// <param name="logger">
    /// Optional logger; forwarded to <see cref="SnowflakeMeshNodeReader.ReadMeshNode"/> so a
    /// poisoned content payload surfaces as a warning instead of silently degrading.
    /// </param>
    public SnowflakeVersionQuery(
        SnowflakeConnectionSource source,
        string? schema = null,
        IIoPool? ioPool = null,
        ILogger<SnowflakeVersionQuery>? logger = null)
    {
        _source = source;
        _ioPool = ioPool ?? IoPool.Unbounded;
        _logger = logger;
        _historyTable = string.IsNullOrEmpty(schema)
            ? SnowflakeIdentifiers.Quote("mesh_node_history")
            : SnowflakeIdentifiers.Qualify(schema, "mesh_node_history");
    }

    /// <summary>
    /// Splits a mesh path into its <c>(namespace, id)</c> pair — the storage key of the history
    /// table. Identical to the PG original.
    /// </summary>
    private static (string Namespace, string Id) SplitPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var ns = lastSlash > 0 ? path[..lastSlash] : "";
        var id = lastSlash > 0 ? path[(lastSlash + 1)..] : path;
        return (ns, id);
    }

    /// <inheritdoc />
    public IObservable<MeshNodeVersion> GetVersions(string path)
        // The Snowflake I/O pool runs the DB fetch on the ThreadPool behind its concurrency gate
        // with ConfigureAwait(false) — no custom TaskScheduler (Orleans) is ever captured.
        => _ioPool.Invoke(ct => FetchVersionsAsync(path, ct))
            .SelectMany(versions => versions.ToObservable());

    /// <summary>
    /// I/O leaf for <see cref="GetVersions"/>: fetches every version summary for a node,
    /// newest first. Runs inside the pool — never call directly from a hub scheduler.
    /// </summary>
    private async Task<List<MeshNodeVersion>> FetchVersionsAsync(string path, CancellationToken ct)
    {
        var results = new List<MeshNodeVersion>();
        var (ns, id) = SplitPath(path);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT "version", "last_modified", "changed_by", "name", "node_type"
            FROM {_historyTable} WHERE "namespace" = :p1 AND "id" = :p2
            ORDER BY "version" DESC
            """;
        SnowflakeConnectionSource.AddParam(cmd, "p1", ns, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "p2", id, DbType.String);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MeshNodeVersion(
                path,
                // NUMBER(19,0) may surface as long or decimal depending on driver metadata.
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                ReadUtcTimestamp(reader, 1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersion(string path, long version, JsonSerializerOptions options)
        => _ioPool.Invoke(async ct =>
        {
            var (ns, id) = SplitPath(path);
            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT "id", "namespace", "name", "node_type", "category", "icon", "display_order",
                       "last_modified", "version", "state", "content", "desired_id", "main_node"
                FROM {_historyTable} WHERE "namespace" = :p1 AND "id" = :p2 AND "version" = :p3
                """;
            SnowflakeConnectionSource.AddParam(cmd, "p1", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p2", id, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p3", version, DbType.Int64);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return (MeshNode?)null;

            return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        });

    /// <inheritdoc />
    public IObservable<MeshNode?> GetVersionBefore(string path, long beforeVersion, JsonSerializerOptions options)
        => _ioPool.Invoke(async ct =>
        {
            var (ns, id) = SplitPath(path);
            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT "id", "namespace", "name", "node_type", "category", "icon", "display_order",
                       "last_modified", "version", "state", "content", "desired_id", "main_node"
                FROM {_historyTable} WHERE "namespace" = :p1 AND "id" = :p2 AND "version" < :p3
                ORDER BY "version" DESC LIMIT 1
                """;
            SnowflakeConnectionSource.AddParam(cmd, "p1", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p2", id, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p3", beforeVersion, DbType.Int64);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return (MeshNode?)null;

            return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        });

    /// <inheritdoc />
    public IObservable<MeshNode> WriteVersion(MeshNode node, JsonSerializerOptions options)
        => _ioPool.Invoke(async ct =>
        {
            var ns = node.Namespace ?? "";
            var contentJson = node.Content != null
                ? JsonSerializer.Serialize(node.Content, node.Content.GetType(), options)
                : null;

            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            // Dialect mapping vs the PG original:
            //  - ON CONFLICT (namespace, id, version) DO NOTHING → INSERT … SELECT … WHERE NOT
            //    EXISTS on the same key (Snowflake enforces no unique constraints; the dummy
            //    one-row FROM keeps the WHERE clause portable across the emulator's transpiler).
            //  - $12::jsonb → PARSE_JSON(:p12) (VARIANT). PARSE_JSON of a NULL bind yields SQL NULL.
            //  - "path" is a REAL NOT NULL column here (PG generates it); it is computed inline
            //    with exactly the PG generation semantics so both backends stay row-identical.
            cmd.CommandText = $"""
                INSERT INTO {_historyTable} (
                    "namespace", "id", "path", "name", "node_type", "description", "category", "icon",
                    "display_order", "last_modified", "version", "state", "content", "desired_id", "main_node"
                )
                SELECT :p1, :p2,
                       CASE WHEN :p1 = '' THEN :p2 ELSE :p1 || '/' || :p2 END,
                       :p3, :p4, :p5, :p6, :p7, :p8, :p9, :p10, :p11, PARSE_JSON(:p12), :p13, :p14
                FROM (SELECT 1 AS "x")
                WHERE NOT EXISTS (
                    SELECT 1 FROM {_historyTable}
                    WHERE "namespace" = :p1 AND "id" = :p2 AND "version" = :p10)
                """;
            SnowflakeConnectionSource.AddParam(cmd, "p1", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p2", node.Id, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p3", node.Name, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p4", node.NodeType, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p5", null, DbType.String); // description
            SnowflakeConnectionSource.AddParam(cmd, "p6", node.Category, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p7", node.Icon, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p8", node.Order, DbType.Int32);
            // TIMESTAMP_NTZ stores UTC by this backend's contract — always bind UtcDateTime.
            SnowflakeConnectionSource.AddParam(cmd, "p9", node.LastModified.UtcDateTime, DbType.DateTime);
            SnowflakeConnectionSource.AddParam(cmd, "p10", node.Version, DbType.Int64);
            SnowflakeConnectionSource.AddParam(cmd, "p11", (short)node.State, DbType.Int16);
            SnowflakeConnectionSource.AddParam(cmd, "p12", contentJson, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p13", node.DesiredId, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "p14", node.MainNode, DbType.String);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return node;
        });

    /// <summary>
    /// Reads a UTC <see cref="DateTimeOffset"/> from a <c>TIMESTAMP_NTZ</c> column, which stores
    /// UTC by this backend's contract and typically surfaces as a <see cref="DateTime"/> with
    /// <see cref="DateTimeKind.Unspecified"/> — re-stamped as UTC here. Defensive against
    /// driver/endpoint variance (a <c>TIMESTAMP_TZ</c>-mapping emulator may hand back a
    /// <see cref="DateTimeOffset"/> directly), mirroring the coercion in
    /// <see cref="SnowflakeMeshNodeReader"/>.
    /// </summary>
    private static DateTimeOffset ReadUtcTimestamp(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Utc),
                TimeSpan.Zero)
        };
    }
}
