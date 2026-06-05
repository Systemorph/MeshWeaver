using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
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
        DisposeTrackedSchemaDataSources();
        DataSource?.Dispose();
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
        => Observable.FromAsync(token => CreateSchemaAdapterAsync(schemaName, partitionDef, token));

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
    /// <see cref="IObservable{T}"/> projection of <see cref="CleanDataAsync"/>
    /// so test bodies stay void + blocking-reactive (§2a). The DELETE statements
    /// (low-level PG ops) stay async inside.
    /// </summary>
    public IObservable<Unit> CleanData()
        => Observable.FromAsync(async () => { await CleanDataAsync(); return Unit.Default; });

    /// <summary>
    /// Cleans all data tables for test isolation.
    /// </summary>
    public async Task CleanDataAsync()
    {
        // Release per-schema pool connections first so the DELETE statements
        // don't compete with leaked schema adapters.
        DisposeTrackedSchemaDataSources();

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

        // Per-partition schemas (orga, orgb, testorg, …) carry their own
        // mesh_nodes + satellite tables that survive prior tests in the same
        // collection. Without this, threads in `orga.threads` leak from
        // ThreadPathResolutionTest into UserActivityCrossPartitionTests,
        // throwing off cross-schema UNION counts. Discover every non-system
        // schema and DELETE from any data tables it carries.
        await using (var listSchemas = DataSource.CreateCommand(
            """
            SELECT schema_name FROM information_schema.schemata
            WHERE schema_name NOT IN ('public', 'pg_catalog', 'information_schema',
                                       'pg_toast', 'pg_temp_1', 'pg_toast_temp_1')
              AND schema_name NOT LIKE 'pg\_%'
            """))
        {
            var schemas = new List<string>();
            await using (var rdr = await listSchemas.ExecuteReaderAsync())
            {
                while (await rdr.ReadAsync())
                    schemas.Add(rdr.GetString(0));
            }
            foreach (var schema in schemas)
            {
                var qs = "\"" + schema.Replace("\"", "\"\"") + "\"";
                // Each per-partition schema MAY have these tables — IF EXISTS
                // (via DO blocks) keeps the cleanup tolerant of partial schemas.
                await using var schemaCmd = DataSource.CreateCommand($"""
                    DO $$ BEGIN
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'mesh_nodes')
                      THEN EXECUTE 'DELETE FROM {qs}.mesh_nodes';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'threads')
                      THEN EXECUTE 'DELETE FROM {qs}.threads';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'activities')
                      THEN EXECUTE 'DELETE FROM {qs}.activities';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'user_activities')
                      THEN EXECUTE 'DELETE FROM {qs}.user_activities';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'access')
                      THEN EXECUTE 'DELETE FROM {qs}.access';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'annotations')
                      THEN EXECUTE 'DELETE FROM {qs}.annotations';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'notifications')
                      THEN EXECUTE 'DELETE FROM {qs}.notifications';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'code')
                      THEN EXECUTE 'DELETE FROM {qs}.code';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'partition_objects')
                      THEN EXECUTE 'DELETE FROM {qs}.partition_objects';
                      END IF;
                      IF EXISTS (SELECT 1 FROM information_schema.tables
                                 WHERE table_schema = '{schema.Replace("'", "''")}'
                                   AND table_name = 'user_effective_permissions')
                      THEN EXECUTE 'DELETE FROM {qs}.user_effective_permissions';
                      END IF;
                    END $$;
                    """);
                await schemaCmd.ExecuteNonQueryAsync();
            }
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
