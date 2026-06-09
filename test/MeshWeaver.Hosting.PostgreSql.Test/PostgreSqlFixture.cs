using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Shared fixture that starts a PostgreSQL container with pgvector extension
/// and initializes the schema once per test collection.
/// </summary>
public class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;
    public PostgreSqlStorageAdapter StorageAdapter { get; private set; } = null!;
    public PostgreSqlAccessControl AccessControl { get; private set; } = null!;
    public PostgreSqlStorageOptions Options { get; private set; } = new();

    // Per-schema data sources created via CreateSchemaAdapterAsync — tracked so
    // CleanDataAsync (called between tests) can dispose them and release
    // physical PG connections back to the container. Without this, each test
    // leaks 1 connection per schema; CrossPartitionSearchTests + the access
    // batches pushed past max_connections=100 even with MaxPoolSize=1.
    private readonly System.Collections.Concurrent.ConcurrentBag<NpgsqlDataSource>
        _trackedSchemaDataSources = new();

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("meshweaver_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
        dataSourceBuilder.UseVector();
        DataSource = dataSourceBuilder.Build();

        // Initialize schema + partition_access + searchable_schemas + stored proc
        Options = new PostgreSqlStorageOptions();
        await PostgreSqlSchemaInitializer.InitializeAsync(DataSource, Options);
        await PostgreSqlSchemaInitializer.InitializePartitionAccessTableAsync(DataSource);

        // Framework schemas the migration creates eagerly in prod (SchemaInitialization).
        // The storage router no longer lazily CREATE SCHEMAs, so these must exist up front:
        //   auth          — V27 access-object mirror (trigger destination)
        //   system_access — global / root-scope AccessAssignment scope (namespace `_Access`)
        // Mirror the migration so full-mesh tests on this container behave like prod.
        foreach (var frameworkSchema in new[] { "auth", "system_access" })
        {
            await using var cmd = DataSource.CreateCommand("SELECT public.ensure_partition_schema(@p)");
            cmd.Parameters.AddWithValue("p", frameworkSchema);
            await cmd.ExecuteNonQueryAsync();
        }

        StorageAdapter = new PostgreSqlStorageAdapter(DataSource);
        AccessControl = new PostgreSqlAccessControl(DataSource);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeTrackedSchemaDataSourcesAsync();
        if (DataSource is not null)
            await DataSource.DisposeAsync();
        if (_container != null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Creates a per-schema data source and adapter for a named schema.
    /// Initializes the schema with satellite tables if a PartitionDefinition with TableMappings is provided.
    /// </summary>
    public async Task<(NpgsqlDataSource SchemaDataSource, PostgreSqlStorageAdapter Adapter)>
        CreateSchemaAdapterAsync(string schemaName, PartitionDefinition? partitionDef = null, CancellationToken ct = default)
    {
        // Create schema
        await using (var cmd = DataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\""))
            await cmd.ExecuteNonQueryAsync(ct);

        // Create per-schema data source with a SINGLE-connection pool. Default
        // MaxPoolSize=100 multiplied across ~30 per-test schema activations
        // exhausts the Postgres container's max_connections=100 cap and every
        // subsequent schema-init hits `53300: sorry, too many clients
        // already`. Drop to 1 so each schema holds at most one live connection.
        // Npgsql requires ConnectionIdleLifetime >= ConnectionPruningInterval
        // (default 10s) — leave it at default so we don't trip
        // ArgumentException at DataSource build.
        //
        // Long-term: the per-(schema, table) PartitionStorageHub architecture
        // (Doc/Architecture/PartitionStorageHubs.md) replaces this entirely
        // with single-connection actors. This is the tactical CI unblock.
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            SearchPath = $"{schemaName},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dsBuilder.UseVector();
        var schemaDs = dsBuilder.Build();

        // Initialize mesh_nodes table in the schema — pass schema name so that
        // rebuild_user_effective_permissions() gets the correct search_path hardcoded.
        var schemaOptions = new PostgreSqlStorageOptions
        {
            VectorDimensions = Options.VectorDimensions,
            Schema = schemaName
        };
        await PostgreSqlSchemaInitializer.InitializeAsync(schemaDs, schemaOptions);

        // Create satellite tables if partition definition has mappings
        if (partitionDef?.TableMappings is { Count: > 0 })
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                schemaDs, schemaOptions, partitionDef.TableMappings.Values, ct);
        }

        var adapter = new PostgreSqlStorageAdapter(schemaDs, partitionDefinition: partitionDef);
        _trackedSchemaDataSources.Add(schemaDs);
        return (schemaDs, adapter);
    }

    /// <summary>
    /// <see cref="IObservable{T}"/> projection of <see cref="CreateSchemaAdapterAsync"/>
    /// so test bodies stay void + blocking-reactive (§2a). The low-level schema
    /// DDL stays async inside; this only wraps it via
    /// <see cref="Observable.FromAsync{TResult}(System.Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{TResult}})"/>.
    /// </summary>
    public IObservable<(NpgsqlDataSource SchemaDataSource, PostgreSqlStorageAdapter Adapter)>
        CreateSchemaAdapter(string schemaName, PartitionDefinition? partitionDef = null, CancellationToken ct = default)
        => IoPool.Unbounded.Invoke(token => CreateSchemaAdapterAsync(schemaName, partitionDef, token));

    /// <summary>
    /// Disposes every per-schema NpgsqlDataSource ever returned by
    /// <see cref="CreateSchemaAdapterAsync"/>. Call between tests so the
    /// container doesn't run out of connections (max_connections=100).
    /// Returned data sources can still be referenced by the caller after
    /// dispose — they just won't pool new connections.
    /// </summary>
    public void DisposeTrackedSchemaDataSources()
    {
        while (_trackedSchemaDataSources.TryTake(out var ds))
        {
            try { ds.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Async counterpart of <see cref="DisposeTrackedSchemaDataSources"/>.
    /// <see cref="NpgsqlDataSource.DisposeAsync"/> releases the pooled physical
    /// connections back to the server promptly; the synchronous <c>Dispose()</c>
    /// can leave them lingering (pending async returns), which under the sharded
    /// CI run packs enough live connections to trip <c>53300: too many clients</c>.
    /// Awaited from <see cref="CleanDataAsync"/> (between tests) and
    /// <see cref="DisposeAsync"/> so connections free upon dispose.
    /// </summary>
    public async Task DisposeTrackedSchemaDataSourcesAsync()
    {
        while (_trackedSchemaDataSources.TryTake(out var ds))
        {
            try { await ds.DisposeAsync(); } catch { /* tearing down */ }
        }
    }

    /// <summary>
    /// <see cref="IObservable{T}"/> projection of <see cref="CleanDataAsync"/>
    /// so test bodies stay void + blocking-reactive (§2a). The DELETE statements
    /// (low-level PG ops) stay async inside.
    /// </summary>
    public IObservable<Unit> CleanData()
        => IoPool.Unbounded.Invoke(async _ => { await CleanDataAsync(); return Unit.Default; });

    /// <summary>
    /// Cleans all data tables for test isolation.
    /// </summary>
    public async Task CleanDataAsync()
    {
        // Release per-schema pool connections first so the DELETE statements
        // don't compete with leaked schema adapters. Async-dispose so the physical
        // connections actually return to the server (sync Dispose can leave them
        // pending → 53300: too many clients under the sharded run).
        await DisposeTrackedSchemaDataSourcesAsync();

        // 7 DELETEs in one round-trip. TRUNCATE looks tempting but is ~3× slower
        // here: tests use tiny tables (a handful of rows each), so DELETE's
        // per-row cost is below TRUNCATE's fixed per-call overhead (ACCESS
        // EXCLUSIVE lock acquisition + new heap file allocation × 7 tables).
        // Verified 2026-05-23: 5.6s → 17.2s on QueryTests when this was
        // TRUNCATE.
        await using var cmd = DataSource.CreateCommand(
            """
            DELETE FROM partition_objects;
            DELETE FROM mesh_nodes;
            DELETE FROM user_effective_permissions;
            DELETE FROM user_effective_permissions_shadow;
            DELETE FROM access_control;
            DELETE FROM group_members;
            DELETE FROM node_type_permissions;
            """);
        await cmd.ExecuteNonQueryAsync();

        // Per-partition schemas (orga, orgb, testorg, …) carry their own mesh_nodes +
        // satellite tables that survive prior tests in the same collection (threads in
        // `orga.threads` would otherwise leak across tests and skew cross-schema UNION
        // counts). The previous shape ran ~10 information_schema existence-probes PER
        // schema PER test inside a DO block; since test partition schemas accumulate
        // through the collection (they are never dropped), cleanup was O(10 × schemas)
        // and every test got slower as the suite progressed — QuerySyntaxTests measured
        // 0.12s/test early vs 2.5s/test late (20×), which is most of the full suite's
        // wall-clock. Instead: ONE catalog query resolves all (schema, data-table) pairs,
        // then a single batched DELETE. Same tables, same isolation, O(1) catalog probing.
        // DELETE on the tiny/empty tables is ~free — the per-schema probing was the cost.
        var perSchemaTables = new[]
        {
            "mesh_nodes", "threads", "activities", "user_activities", "access",
            "annotations", "notifications", "code", "partition_objects",
            "user_effective_permissions"
        };
        var targets = new List<(string Schema, string Table)>();
        await using (var listTables = DataSource.CreateCommand(
            """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_name = ANY($1)
              AND table_schema NOT IN ('public', 'pg_catalog', 'information_schema', 'pg_toast')
              AND table_schema NOT LIKE 'pg\_%'
            """))
        {
            listTables.Parameters.AddWithValue(perSchemaTables);
            await using var rdr = await listTables.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                targets.Add((rdr.GetString(0), rdr.GetString(1)));
        }
        if (targets.Count > 0)
        {
            var sb = new System.Text.StringBuilder(targets.Count * 48);
            foreach (var (schema, table) in targets)
                sb.Append("DELETE FROM \"").Append(schema.Replace("\"", "\"\""))
                  .Append("\".\"").Append(table.Replace("\"", "\"\"")).Append("\";\n");
            await using var del = DataSource.CreateCommand(sb.ToString());
            await del.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>
/// Isolated PostgreSQL container reserved for tests that observe the
/// change feed (pg_notify LISTEN/NOTIFY pipeline). The default shared
/// fixture is used by ~25 test classes; some of those write data to
/// partition schemas (acme/futur/contoso/…) and trigger pg_notify events
/// on the same DataSource a LISTEN-based test is subscribed to. The
/// listener then receives changes from those neighbour tests and
/// Query's "scope" filter — which guards on namespace prefix —
/// is challenged in ways the test was not designed for (e.g. extra
/// emissions on rapid cross-namespace writes). Splitting these tests
/// into their own collection gives them a clean container and a
/// LISTEN session that only ever sees their own writes.
/// </summary>
public class IsolatedPostgreSqlFixture : PostgreSqlFixture;

/// <summary>
/// Dedicated collection definition for LISTEN/NOTIFY-sensitive tests so
/// they get an <see cref="IsolatedPostgreSqlFixture"/> separate from the
/// shared one used by write-heavy partition tests. See
/// <see cref="IsolatedPostgreSqlFixture"/> for motivation.
/// </summary>
[CollectionDefinition("PostgreSqlIsolated")]
public class IsolatedPostgreSqlCollection : ICollectionFixture<IsolatedPostgreSqlFixture>;

[CollectionDefinition("PostgreSql")]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;
