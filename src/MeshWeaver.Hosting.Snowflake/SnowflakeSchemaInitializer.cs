using System.Collections.Immutable;
using System.Data.Common;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Creates the Snowflake schema objects (schemas + tables) if not already present — the Snowflake
/// port of <c>PostgreSqlSchemaInitializer</c>. Table and column names are byte-identical to the
/// PostgreSQL backend so the SQL generator / query layer works against either backend unchanged.
///
/// <para><b>Statement-at-a-time.</b> Snowflake DDL autocommits per statement and the driver does
/// not batch multi-statement scripts, so the public surface returns LISTS of individual SQL
/// statements; <see cref="InitializeAsync"/> / <see cref="EnsurePartitionSchemaAsync"/> execute
/// them sequentially on one connection. Every statement is <c>CREATE … IF NOT EXISTS</c>, so
/// re-running is idempotent.</para>
///
/// <para><b>Dialect mapping</b> (vs the PG DDL): <c>JSONB</c> → <c>VARIANT</c>;
/// <c>TIMESTAMPTZ</c> → <c>TIMESTAMP_NTZ</c> storing UTC (defaults use <c>SYSDATE()</c>, which
/// returns the current UTC time as <c>TIMESTAMP_NTZ</c> — the correct "UTC in NTZ" twin of PG's
/// <c>DEFAULT NOW()</c>); <c>BIGINT</c> → <c>NUMBER(19,0)</c>; <c>SMALLINT</c> →
/// <c>NUMBER(5,0)</c>; <c>BIGSERIAL</c> → <c>NUMBER(19,0) IDENTITY(1,1)</c>;
/// <c>vector(N)</c> → <c>VECTOR(FLOAT, N)</c> (emitted only when the endpoint supports it —
/// see <see cref="DetectVectorSupportAsync"/>). Every identifier is double-quoted lowercase via
/// <see cref="SnowflakeIdentifiers"/> because Snowflake uppercases unquoted identifiers while the
/// mesh's path router produces lowercase schema names identical to the PG backend.</para>
///
/// <para><b>Deliberately NOT ported from the PG script</b> (Snowflake has none of these; the
/// behaviors move into the C# write path / other components):
/// <list type="bullet">
///   <item>All secondary indexes (<c>CREATE INDEX</c>, GIN/HNSW/tsvector) — Snowflake has no
///     secondary indexes; micro-partition pruning + the optional vector type cover the reads.</item>
///   <item>All plpgsql functions, stored procedures and triggers
///     (<c>notify_mesh_node_changes</c>/pg_notify, <c>trg_mesh_node_to_history</c>,
///     <c>rebuild_user_effective_permissions</c>/<c>rebuild_user_permissions_for</c>/
///     <c>trg_access_changed</c>, <c>mirror_access_object_to_auth_schema</c>,
///     <c>ensure_partition_schema</c>, <c>search_across_schemas</c>) — history/permission
///     projection/auth-mirroring/change publication run in the C# write path; cross-process
///     change propagation polls <c>events.event_log</c> instead of LISTEN/NOTIFY.</item>
///   <item>The <c>public.top_level_index</c> materialized view + <c>rebuild_top_level_index</c>
///     — owned by the Snowflake cross-schema query layer, not the initializer.</item>
///   <item><c>CREATE EXTENSION vector</c> and the embedding-dimension <c>ALTER</c> migration
///     block — <c>VECTOR</c> is built in; dimension changes are a migration concern.</item>
///   <item><c>user_effective_permissions_shadow</c> — it exists solely to support the plpgsql
///     atomic rename-swap rebuild; the C# permission projection writes
///     <c>user_effective_permissions</c> directly (transactional delete+insert).</item>
/// </list></para>
///
/// <para><b>Constraints document intent only.</b> Snowflake accepts but does not enforce
/// <c>PRIMARY KEY</c>/<c>UNIQUE</c> (only <c>NOT NULL</c> is enforced); the clauses are kept
/// wherever PG has them so the key shape stays visible, and the C# write path (MERGE-based
/// upserts keyed on the same columns) provides the actual uniqueness.</para>
/// </summary>
public static class SnowflakeSchemaInitializer
{
    /// <summary>
    /// The probe statement proving the endpoint supports the <c>VECTOR(FLOAT, N)</c> type —
    /// real Snowflake does; the LocalStack emulator may not.
    /// </summary>
    private const string VectorProbeSql = "SELECT PARSE_JSON('[1,2]')::VECTOR(FLOAT, 2)";

    /// <summary>
    /// All DDL statements provisioning one partition: <c>CREATE SCHEMA IF NOT EXISTS</c> plus
    /// every per-partition table the PG partition script
    /// (<c>PostgreSqlSchemaInitializer.GetVersionedPartitionDdl</c> +
    /// <c>GetSatelliteTableScript</c>, as run by <c>public.ensure_partition_schema</c>) creates:
    /// <c>mesh_nodes</c>, <c>partition_objects</c>, <c>user_activity</c>, <c>change_logs</c>,
    /// <c>user_effective_permissions</c>, <c>access_control</c>, <c>group_members</c>,
    /// <c>node_type_permissions</c>, <c>mesh_node_history</c>, and the standard satellite tables
    /// from <see cref="SatelliteTableMapping.Defaults"/> (<c>activities</c>,
    /// <c>user_activities</c>, <c>threads</c>, <c>access</c>, <c>annotations</c>,
    /// <c>notifications</c>, <c>code</c>).
    /// </summary>
    /// <param name="schema">
    /// The partition schema name — the caller (the Snowflake partition storage provider) passes
    /// the already-lowercased first path segment, exactly as the PG router does. The name is
    /// emitted verbatim (quoted), never case-folded here.
    /// </param>
    /// <param name="vectorDimensions">Embedding dimensions for the <c>embedding</c> column.</param>
    /// <param name="vectorEnabled">
    /// Whether the <c>"embedding" VECTOR(FLOAT, N)</c> column is emitted at all. Pass the probed
    /// capability (<see cref="DetectVectorSupportAsync"/> / <see cref="SnowflakeCapabilities.SupportsVector"/>).
    /// </param>
    /// <returns>Individual SQL statements, in dependency order, each independently idempotent.</returns>
    public static IReadOnlyList<string> GetPartitionStatements(string schema, int vectorDimensions, bool vectorEnabled)
    {
        var statements = ImmutableList.CreateBuilder<string>();
        statements.Add($"CREATE SCHEMA IF NOT EXISTS {SnowflakeIdentifiers.Quote(schema)}");
        statements.Add(GetNodeTableStatement(schema, "mesh_nodes", withSyncBehavior: true, vectorDimensions, vectorEnabled));
        statements.Add(GetPartitionObjectsStatement(schema));
        statements.Add(GetUserActivityStatement(schema));
        statements.Add(GetChangeLogsStatement(schema));
        statements.Add(GetUserEffectivePermissionsStatement(schema));
        statements.Add(GetAccessControlStatement(schema));
        statements.Add(GetGroupMembersStatement(schema));
        statements.Add(GetNodeTypePermissionsStatement(schema));
        statements.Add(GetMeshNodeHistoryStatement(schema));
        foreach (var table in SatelliteTableMapping.Defaults
                     .Select(m => m.Table)
                     .Distinct(StringComparer.Ordinal))
            statements.AddRange(GetSatelliteTableStatements(schema, table, vectorDimensions, vectorEnabled));
        return statements.ToImmutable();
    }

    /// <summary>
    /// DDL for one satellite table (same row shape as <c>mesh_nodes</c>), the Snowflake port of
    /// <c>PostgreSqlSchemaInitializer.GetSatelliteTableScript</c> minus the indexes and the
    /// pg_notify trigger. Exposed per-table so callers can provision any satellite a custom
    /// <c>SnowflakeStorageOptions.SatelliteTables</c> mapping introduces.
    /// <para>The <c>access</c> table is special-cased to carry <c>sync_behavior</c>: in PG the
    /// versioned-partition DDL creates <c>access</c> WITH the column before the generic satellite
    /// script no-ops on it, so the live PG shape of <c>access</c> always has it. Emitting the same
    /// shape here keeps the two backends column-identical regardless of which entry point creates
    /// the table.</para>
    /// </summary>
    /// <param name="schema">The (lowercased) partition schema name, emitted verbatim.</param>
    /// <param name="table">The satellite table name (e.g. <c>threads</c>).</param>
    /// <param name="vectorDimensions">Embedding dimensions for the <c>embedding</c> column.</param>
    /// <param name="vectorEnabled">Whether the <c>embedding</c> column is emitted.</param>
    /// <returns>Individual SQL statements for this satellite table.</returns>
    public static IReadOnlyList<string> GetSatelliteTableStatements(
        string schema, string table, int vectorDimensions, bool vectorEnabled)
        => ImmutableList.Create(GetNodeTableStatement(
            schema, table,
            withSyncBehavior: string.Equals(table, "access", StringComparison.Ordinal),
            vectorDimensions, vectorEnabled));

    /// <summary>
    /// All DDL statements for the central (cross-partition) objects: the central schema
    /// (default <c>public</c>) with <c>partition_access</c> + <c>searchable_schemas</c>
    /// (column shapes mirror PG's <c>InitializePartitionAccessTableAsync</c>), and the events
    /// schema (default <c>events</c>) with the durable <c>event_log</c> outbox + the per-consumer
    /// <c>action_cursor</c> (column shapes mirror the PG <c>V40_CreateEventLogSchema</c>
    /// migration, plus <c>origin_id</c> so the change-feed poller can drop its own silo's echoes —
    /// Snowflake has no LISTEN/NOTIFY, so cross-process change propagation polls this log).
    /// <para>Unlike PG's <c>InitializeAsync</c>, this does NOT provision the central schema's own
    /// partition tables (PG runs the full partition DDL against <c>public</c>); compose
    /// <see cref="EnsurePartitionSchemaAsync"/> for <see cref="SnowflakeStorageOptions.Schema"/>
    /// when the central schema doubles as a partition.</para>
    /// </summary>
    /// <param name="options">Supplies the central and events schema names.</param>
    /// <returns>Individual SQL statements, in dependency order, each independently idempotent.</returns>
    public static IReadOnlyList<string> GetCentralStatements(SnowflakeStorageOptions options)
    {
        var central = options.Schema;
        var events = options.EventsSchema;
        return ImmutableList.Create(
            $"CREATE SCHEMA IF NOT EXISTS {SnowflakeIdentifiers.Quote(central)}",
            $"""
            CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(central, "partition_access")} (
                "user_id"   TEXT NOT NULL,
                "partition" TEXT NOT NULL,
                PRIMARY KEY ("user_id", "partition")
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(central, "searchable_schemas")} (
                "schema_name" TEXT PRIMARY KEY
            )
            """,
            $"CREATE SCHEMA IF NOT EXISTS {SnowflakeIdentifiers.Quote(events)}",
            $"""
            CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(events, "event_log")} (
                "seq"         NUMBER(19,0) IDENTITY(1,1) NOT NULL PRIMARY KEY,
                "occurred_at" TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
                "namespace"   TEXT NOT NULL DEFAULT '',
                "path"        TEXT NOT NULL,
                "node_type"   TEXT,
                "kind"        TEXT NOT NULL,
                "version"     NUMBER(19,0) NOT NULL DEFAULT 0,
                -- Originating silo id: the change-feed poller skips events it appended itself
                -- (in-process subscribers were already notified synchronously from Write/Delete).
                "origin_id"   TEXT,
                -- JSON-serialized MeshChangeEvent. TEXT (not VARIANT) deliberately: the store
                -- binds/reads it as an opaque JSON string; VARIANT would re-shape it on write
                -- (implicit string→VARIANT stores a quoted scalar) and complicate the read-back.
                "payload"     TEXT,
                -- Unenforced in Snowflake — documents Append's idempotency key; the C# writer
                -- enforces it via INSERT ... WHERE NOT EXISTS on (path, kind, version).
                CONSTRAINT "uq_event_log_path_kind_version" UNIQUE ("path", "kind", "version")
            )
            """,
            $"""
            CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(events, "action_cursor")} (
                "consumer_id" TEXT PRIMARY KEY,
                "last_seq"    NUMBER(19,0) NOT NULL DEFAULT 0
            )
            """);
    }

    /// <summary>
    /// Initializes the central (cross-partition) objects — <see cref="GetCentralStatements"/> —
    /// executing each statement sequentially on ONE connection. Idempotent; safe to run on every
    /// boot. Async I/O leaf: callers run it inside an <c>IIoPool</c> invoke, never on a hub
    /// scheduler.
    /// </summary>
    /// <param name="source">The connection source to open against.</param>
    /// <param name="options">Supplies the central and events schema names.</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InitializeAsync(
        SnowflakeConnectionSource source,
        SnowflakeStorageOptions options,
        ILogger? logger,
        CancellationToken ct)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteStatementsAsync(connection, GetCentralStatements(options), logger, ct).ConfigureAwait(false);
        logger?.LogInformation(
            "Snowflake central schema initialized (central: {Central}, events: {Events})",
            options.Schema, options.EventsSchema);
    }

    /// <summary>
    /// Provisions one partition — <see cref="GetPartitionStatements"/> — executing each statement
    /// sequentially on ONE connection. Idempotent (every statement is <c>IF NOT EXISTS</c>);
    /// the Snowflake counterpart of PG's <c>SELECT public.ensure_partition_schema(name)</c>.
    /// Async I/O leaf: callers run it inside an <c>IIoPool</c> invoke (the partition storage
    /// provider's promise-cached <c>EnsurePartitionProvisioned</c>), never on a hub scheduler.
    /// </summary>
    /// <param name="source">The connection source to open against.</param>
    /// <param name="schema">The (lowercased) partition schema name.</param>
    /// <param name="vectorDimensions">Embedding dimensions for the <c>embedding</c> columns.</param>
    /// <param name="vectorEnabled">Whether <c>embedding</c> columns are emitted (probed capability).</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsurePartitionSchemaAsync(
        SnowflakeConnectionSource source,
        string schema,
        int vectorDimensions,
        bool vectorEnabled,
        ILogger? logger,
        CancellationToken ct)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteStatementsAsync(
            connection, GetPartitionStatements(schema, vectorDimensions, vectorEnabled), logger, ct)
            .ConfigureAwait(false);
        logger?.LogInformation(
            "Snowflake partition schema {Schema} provisioned (vector: {VectorEnabled})",
            schema, vectorEnabled);
    }

    /// <summary>
    /// Probes whether the endpoint supports the <c>VECTOR(FLOAT, N)</c> type by executing
    /// <c>SELECT PARSE_JSON('[1,2]')::VECTOR(FLOAT, 2)</c>. Real Snowflake supports it; the
    /// LocalStack emulator may not — a <see cref="DbException"/> means "no vector support"
    /// (embedding columns are then omitted and free-text queries stay on the ILIKE path).
    /// </summary>
    /// <param name="source">The connection source to open against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the probe succeeds; <c>false</c> on a <see cref="DbException"/>.</returns>
    public static async Task<bool> DetectVectorSupportAsync(SnowflakeConnectionSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = VectorProbeSql;
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <paramref name="statements"/> one at a time on <paramref name="connection"/> —
    /// Snowflake DDL autocommits per statement and the driver does not support multi-statement
    /// batches, so sequential single-statement execution IS the transaction model here.
    /// </summary>
    private static async Task ExecuteStatementsAsync(
        DbConnection connection,
        IReadOnlyList<string> statements,
        ILogger? logger,
        CancellationToken ct)
    {
        foreach (var sql in statements)
        {
            logger?.LogDebug("Executing Snowflake DDL statement: {Sql}", sql);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The mesh-nodes row shape shared by <c>mesh_nodes</c> and every satellite table — the
    /// Snowflake port of the PG <c>CREATE TABLE mesh_nodes</c> / <c>GetSatelliteTableScript</c>
    /// column list.
    /// <para>🚨 <c>path</c> is a REAL column here. PG declares it
    /// <c>GENERATED ALWAYS AS (CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END) STORED</c>;
    /// Snowflake has no stored generated columns, so the C# write path MUST maintain it with
    /// exactly those semantics on every insert/update:
    /// <c>path = namespace == "" ? id : namespace + "/" + id</c>.</para>
    /// </summary>
    private static string GetNodeTableStatement(
        string schema, string table, bool withSyncBehavior, int vectorDimensions, bool vectorEnabled)
    {
        // Optional column fragments keep the emitted DDL free of dangling commas: each fragment
        // carries its own trailing separator and newline.
        var syncBehaviorColumn = withSyncBehavior
            ? """
                  -- node-level static-repo sync claim (0=Include, 1=ExcludeThisOnly,
                  -- 2=ExcludeThisAndChildren). Persists the "Not synced" decouple so an
                  -- admin-claimed node/partition survives restart + the next import.
                  "sync_behavior"  NUMBER(5,0) NOT NULL DEFAULT 0,

              """
            : string.Empty;
        var embeddingColumn = vectorEnabled
            ? $"""
                   "embedding"      VECTOR(FLOAT, {vectorDimensions}),

               """
            : string.Empty;
        return $"""
            CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, table)} (
                "namespace"      TEXT NOT NULL DEFAULT '',
                "id"             TEXT NOT NULL,
                -- REAL column (PG: GENERATED ALWAYS AS (...) STORED; Snowflake has no stored
                -- generated columns). Maintained by the C# write path as:
                -- CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
                "path"           TEXT NOT NULL,
                "name"           TEXT,
                "node_type"      TEXT,
                "description"    TEXT,
                "category"       TEXT,
                "icon"           TEXT,
                "display_order"  INTEGER,
                "last_modified"  TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
                "version"        NUMBER(19,0) NOT NULL DEFAULT 0,
                "state"          NUMBER(5,0) NOT NULL DEFAULT 0,
            {syncBehaviorColumn}    "content"        VARIANT,
                "desired_id"     TEXT,
                "main_node"      TEXT,
            {embeddingColumn}    PRIMARY KEY ("namespace", "id")
            )
            """;
    }

    /// <summary>The <c>partition_objects</c> store for per-node virtual data-source objects.</summary>
    private static string GetPartitionObjectsStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "partition_objects")} (
            "id"            TEXT NOT NULL,
            "partition_key" TEXT NOT NULL,
            "type_name"     TEXT,
            "data"          VARIANT NOT NULL,
            "last_modified" TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
            PRIMARY KEY ("partition_key", "id")
        )
        """;

    /// <summary>The per-user node-access tracking table (<c>user_activity</c>, PG-identical shape).</summary>
    private static string GetUserActivityStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "user_activity")} (
            "user_id"        TEXT NOT NULL,
            "node_path"      TEXT NOT NULL,
            "activity_type"  NUMBER(5,0) NOT NULL DEFAULT 0,
            "first_accessed" TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
            "last_accessed"  TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
            "access_count"   INTEGER NOT NULL DEFAULT 1,
            "node_name"      TEXT,
            "node_type"      TEXT,
            "namespace"      TEXT,
            PRIMARY KEY ("user_id", "node_path")
        )
        """;

    /// <summary>The bundled activity-log table (<c>change_logs</c>, PG-identical shape).</summary>
    private static string GetChangeLogsStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "change_logs")} (
            "id"           TEXT NOT NULL PRIMARY KEY,
            "hub_path"     TEXT NOT NULL,
            "changed_by"   TEXT,
            "category"     TEXT NOT NULL,
            "start_time"   TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
            "end_time"     TIMESTAMP_NTZ,
            "change_count" INTEGER NOT NULL DEFAULT 0,
            "status"       NUMBER(5,0) NOT NULL DEFAULT 1,
            "messages"     VARIANT
        )
        """;

    /// <summary>
    /// The denormalized effective-permission projection. In PG it is rebuilt server-side by
    /// <c>rebuild_user_effective_permissions()</c>; on Snowflake the C# permission projection
    /// writes it directly (no shadow table — that existed solely for plpgsql's rename-swap).
    /// </summary>
    private static string GetUserEffectivePermissionsStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "user_effective_permissions")} (
            "user_id"          TEXT NOT NULL,
            "node_path_prefix" TEXT NOT NULL,
            "permission"       TEXT NOT NULL,
            "is_allow"         BOOLEAN NOT NULL,
            PRIMARY KEY ("user_id", "node_path_prefix", "permission")
        )
        """;

    /// <summary>The convenience-method ACL table (<c>access_control</c>, PG-identical shape).</summary>
    private static string GetAccessControlStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "access_control")} (
            "node_path"  TEXT NOT NULL,
            "subject"    TEXT NOT NULL,
            "permission" TEXT NOT NULL,
            "is_allow"   BOOLEAN NOT NULL,
            PRIMARY KEY ("node_path", "subject", "permission")
        )
        """;

    /// <summary>The convenience-method group membership table (<c>group_members</c>, PG-identical shape).</summary>
    private static string GetGroupMembersStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "group_members")} (
            "group_name" TEXT NOT NULL,
            "member_id"  TEXT NOT NULL,
            PRIMARY KEY ("group_name", "member_id")
        )
        """;

    /// <summary>Node-type permission flags (populated from DI-registered NodeTypePermission records).</summary>
    private static string GetNodeTypePermissionsStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "node_type_permissions")} (
            "node_type"   TEXT NOT NULL PRIMARY KEY,
            "public_read" BOOLEAN NOT NULL DEFAULT FALSE
        )
        """;

    /// <summary>
    /// Versioned copies of <c>mesh_nodes</c> (PK includes <c>version</c>). PG populates it via
    /// the <c>mesh_node_copy_to_history</c> trigger; on Snowflake the C# write path appends the
    /// history row alongside the main write. Like PG's history table it carries neither
    /// <c>sync_behavior</c> nor <c>embedding</c>, and adds <c>changed_by</c>.
    /// <para><c>path</c> is a real column maintained by the writer — see
    /// <see cref="GetNodeTableStatement"/> for the generation semantics.</para>
    /// </summary>
    private static string GetMeshNodeHistoryStatement(string schema) => $"""
        CREATE TABLE IF NOT EXISTS {SnowflakeIdentifiers.Qualify(schema, "mesh_node_history")} (
            "namespace"      TEXT NOT NULL DEFAULT '',
            "id"             TEXT NOT NULL,
            -- REAL column; maintained by the C# write path as:
            -- CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END
            "path"           TEXT NOT NULL,
            "name"           TEXT,
            "node_type"      TEXT,
            "description"    TEXT,
            "category"       TEXT,
            "icon"           TEXT,
            "display_order"  INTEGER,
            "last_modified"  TIMESTAMP_NTZ NOT NULL DEFAULT SYSDATE(),
            "version"        NUMBER(19,0) NOT NULL DEFAULT 0,
            "state"          NUMBER(5,0) NOT NULL DEFAULT 0,
            "content"        VARIANT,
            "desired_id"     TEXT,
            "changed_by"     TEXT,
            "main_node"      TEXT,
            PRIMARY KEY ("namespace", "id", "version")
        )
        """;
}
