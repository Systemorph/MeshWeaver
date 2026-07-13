using System.Collections.Immutable;
using System.Data;
using System.Reactive;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Owns the Snowflake <c>top_level_index</c> — the cross-partition top-level-node lookup that the
/// PG backend implements as the <c>public.top_level_index</c> MATERIALIZED VIEW plus the
/// <c>rebuild_top_level_index()</c> plpgsql routine. Snowflake has neither materialized views of
/// this shape nor stored routines here, so the index is a PLAIN TABLE rebuilt from C#: one
/// <c>CREATE OR REPLACE TABLE … AS SELECT … UNION ALL …</c> statement (CREATE OR REPLACE is the
/// atomic swap — readers see either the old or the new table, never a partial one).
///
/// <para><b>What the index holds</b> (mirroring the PG matview definition byte-for-byte): one row
/// per searchable partition — exactly the PARTITION ROOT, i.e. the <c>namespace = ''</c> node
/// whose <c>LOWER(id)</c> equals the schema (partition) name. The schema is the lowercased first
/// path segment while a root's id keeps its ORIGINAL case (Space "AgenticPension" lives in schema
/// <c>agenticpension</c>), hence the case-insensitive match. A plain <c>namespace = ''</c> filter
/// would pull every top-level node and collide <c>path</c> values across partitions. The column
/// list is FIXED (and <c>embedding</c> is DELIBERATELY excluded — in PG, including it made the
/// matview block embedding-dimension changes) so the table shape stays stable across rebuilds.</para>
///
/// <para><b>When it rebuilds</b>: like PG, only on partition-set changes (schema init / partition
/// provisioning) — never on the query hot path. The rebuild runs on the cap-1
/// <c>sf:searchinfra</c> I/O pool, so concurrent rebuild requests serialize instead of racing the
/// <c>CREATE OR REPLACE</c>.</para>
/// </summary>
public sealed class SnowflakeSearchInfrastructure
{
    private const string PoolAdapter = "searchinfra";
    private const string IndexTable = "top_level_index";

    /// <summary>
    /// Defensive identifier check for schema names interpolated into the rebuild's UNION SQL.
    /// The names come from <c>searchable_schemas</c>, which this backend itself maintains as
    /// lowercased path segments — anything else is rejected. Immutable constant, not a cache.
    /// </summary>
    private static readonly Regex SchemaNamePattern =
        new("^[a-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The FIXED column shape of <c>top_level_index</c>, mirroring the PG matview's column list
    /// (<c>rebuild_top_level_index()</c>'s <c>cols</c>): every <c>mesh_nodes</c> column except
    /// <c>sync_behavior</c> and <c>embedding</c>, plus <c>path</c>. The Snowflake type of each
    /// column (per <see cref="SnowflakeSchemaInitializer"/>) drives the explicitly-cast NULL
    /// projection used when no partition is registered yet, so the empty table keeps the exact
    /// same shape.
    /// </summary>
    private static readonly ImmutableArray<(string Column, string SqlType)> IndexColumns =
    [
        ("id", "TEXT"),
        ("namespace", "TEXT"),
        ("name", "TEXT"),
        ("node_type", "TEXT"),
        ("description", "TEXT"),
        ("category", "TEXT"),
        ("icon", "TEXT"),
        ("display_order", "INTEGER"),
        ("last_modified", "TIMESTAMP_NTZ"),
        ("version", "NUMBER(19,0)"),
        ("state", "NUMBER(5,0)"),
        ("content", "VARIANT"),
        ("desired_id", "TEXT"),
        ("main_node", "TEXT"),
        ("path", "TEXT")
    ];

    private readonly SnowflakeConnectionSource _source;
    private readonly SnowflakeStorageOptions _options;
    private readonly ILogger? _logger;
    // Cap-1 write pool: rebuilds serialize; the gate IS the single logical connection
    // (the sf:{adapter} idiom — see IoPoolNames.SnowflakeAdapterPrefix).
    private readonly IIoPool _pool;

    /// <summary>
    /// Creates the search infrastructure over the shared connection source.
    /// </summary>
    /// <param name="source">The one place that opens Snowflake connections.</param>
    /// <param name="options">Storage options; <see cref="SnowflakeStorageOptions.Schema"/> locates <c>searchable_schemas</c> and <c>top_level_index</c>.</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="ioPoolRegistry">Mesh-scoped pool registry; when null (bare unit tests) the unbounded fallback pool is used.</param>
    public SnowflakeSearchInfrastructure(
        SnowflakeConnectionSource source,
        SnowflakeStorageOptions options,
        ILogger<SnowflakeSearchInfrastructure>? logger = null,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _source = source;
        _options = options;
        _logger = logger;
        _pool = ioPoolRegistry?.Get(IoPoolNames.SnowflakeAdapterPrefix + PoolAdapter) ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Rebuilds <c>top_level_index</c> from the current <c>searchable_schemas</c> snapshot —
    /// the Snowflake counterpart of PG's <c>SELECT public.rebuild_top_level_index()</c>. Cold
    /// observable running on the cap-1 <c>sf:searchinfra</c> pool: the work starts on Subscribe,
    /// emits one <see cref="Unit"/> on completion, and concurrent rebuilds serialize behind the
    /// pool gate. Call on partition-set changes (schema init, partition provisioning) — never on
    /// the query hot path.
    /// </summary>
    public IObservable<Unit> RebuildTopLevelIndex()
        => _pool.Invoke(RebuildTopLevelIndexAsync);

    /// <summary>
    /// Ensures <c>top_level_index</c> exists, rebuilding it only when the table is missing
    /// (information_schema probe) — for callers that may query before the first partition-set
    /// change has triggered a rebuild. Cold observable on the same cap-1 pool as
    /// <see cref="RebuildTopLevelIndex"/>; a no-op single emission when the table already exists.
    /// </summary>
    public IObservable<Unit> EnsureTopLevelIndex()
        => _pool.Invoke(EnsureTopLevelIndexAsync);

    /// <summary>
    /// I/O leaf of <see cref="RebuildTopLevelIndex"/>: reads the <c>searchable_schemas</c>
    /// snapshot, pre-filters it against ONE information_schema query for schemas that actually
    /// contain a <c>mesh_nodes</c> table (the drop-race guard replacing PG's <c>to_regclass</c>
    /// skip: <c>searchable_schemas</c> can lag a concurrent partition drop, and referencing the
    /// gone table would fail the whole rebuild), then swaps the index in with a single
    /// <c>CREATE OR REPLACE TABLE … AS SELECT … UNION ALL …</c>. Runs inside the pool — never
    /// call directly from a hub scheduler.
    /// </summary>
    internal async Task RebuildTopLevelIndexAsync(CancellationToken ct)
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        // 1) Current searchable-schemas snapshot (maintained by this backend as lowercased
        //    path segments, ordered for a deterministic UNION).
        var snapshot = ImmutableList.CreateBuilder<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT "schema_name" FROM {SnowflakeIdentifiers.Qualify(_options.Schema, "searchable_schemas")}
                ORDER BY "schema_name"
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                snapshot.Add(reader.GetString(0));
        }

        // 2) ONE catalog query for every schema that actually contains a mesh_nodes table.
        //    information_schema identifiers are deliberately emitted UNQUOTED: Snowflake
        //    uppercases unquoted identifiers, which is exactly what resolves them to the
        //    uppercase catalog objects (INFORMATION_SCHEMA.TABLES / TABLE_SCHEMA / TABLE_NAME);
        //    quoting them lowercase — as every non-catalog identifier in this backend is —
        //    would MISS the catalog. The VALUES are case-exact: partition schemas/tables are
        //    created quoted-lowercase, so 'mesh_nodes' and the lowercase schema names match.
        var existing = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT table_schema FROM information_schema.tables WHERE table_name = 'mesh_nodes'";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing.Add(reader.GetString(0));
        }

        // 3) Survivors: listed AND present AND shaped like an identifier this backend produces.
        var surviving = ImmutableList.CreateBuilder<string>();
        foreach (var schema in snapshot)
        {
            if (!IsValidSchemaName(schema))
            {
                // SQL-injection hygiene: the schema name is interpolated into DDL below, so
                // anything not matching the backend's own lowercase-segment shape is refused.
                _logger?.LogWarning(
                    "RebuildTopLevelIndex: skipping invalid schema name {Schema} from searchable_schemas",
                    schema);
                continue;
            }
            if (!existing.Contains(schema))
            {
                // searchable_schemas lagging a concurrent partition drop — skip, don't fail.
                _logger?.LogDebug(
                    "RebuildTopLevelIndex: skipping listed schema {Schema} without a mesh_nodes table",
                    schema);
                continue;
            }
            surviving.Add(schema);
        }

        // 4) One branch per surviving schema — exactly the PARTITION ROOT (see class docs).
        var columnList = string.Join(", ", IndexColumns.Select(c => SnowflakeIdentifiers.Quote(c.Column)));
        var unionSql = surviving.Count > 0
            ? string.Join(" UNION ALL ", surviving.Select(schema =>
                $"SELECT {columnList} FROM {SnowflakeIdentifiers.Qualify(schema, "mesh_nodes")} " +
                // Regex-validated ([a-z0-9_] only), quote-escaped anyway — belt and braces.
                $"WHERE \"namespace\" = '' AND LOWER(\"id\") = '{schema.Replace("'", "''")}'"))
            // No partitions registered yet — a typed empty select (explicitly-cast NULL columns)
            // keeps the table shape identical to the populated one. The dummy one-row FROM keeps
            // the WHERE portable across the emulator's transpiler.
            : "SELECT "
              + string.Join(", ", IndexColumns.Select(c =>
                  $"CAST(NULL AS {c.SqlType}) AS {SnowflakeIdentifiers.Quote(c.Column)}"))
              + " FROM (SELECT 1 AS \"x\") WHERE FALSE";

        // 5) Atomic swap: CREATE OR REPLACE TABLE replaces the old index in one statement —
        //    readers see either the previous or the new table, never a partial rebuild.
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                $"CREATE OR REPLACE TABLE {SnowflakeIdentifiers.Qualify(_options.Schema, IndexTable)} AS {unionSql}";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger?.LogInformation(
            "Rebuilt {Schema}.top_level_index over {Count} partition schema(s)",
            _options.Schema, surviving.Count);
    }

    /// <summary>
    /// I/O leaf of <see cref="EnsureTopLevelIndex"/>: probes information_schema for the index
    /// table and runs a full rebuild only when it is missing.
    /// </summary>
    private async Task EnsureTopLevelIndexAsync(CancellationToken ct)
    {
        bool exists;
        await using (var connection = await _source.OpenAsync(ct).ConfigureAwait(false))
        {
            await using var cmd = connection.CreateCommand();
            // Unquoted catalog identifiers on purpose — see RebuildTopLevelIndexAsync step 2.
            cmd.CommandText =
                "SELECT 1 FROM information_schema.tables WHERE table_schema = :schema AND table_name = :table";
            SnowflakeConnectionSource.AddParam(cmd, "schema", _options.Schema, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "table", IndexTable, DbType.String);
            exists = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not (null or DBNull);
        }

        if (!exists)
            await RebuildTopLevelIndexAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="schema"/> matches the only shape this backend ever writes into
    /// <c>searchable_schemas</c> — a lowercased path segment (<c>^[a-z0-9_]+$</c>). Everything
    /// else is rejected before being interpolated into rebuild DDL.
    /// </summary>
    internal static bool IsValidSchemaName(string schema)
        => !string.IsNullOrEmpty(schema) && SchemaNamePattern.IsMatch(schema);
}
