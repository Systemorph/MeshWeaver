using System;
using System.Collections.Generic;
using System.Linq;
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

    // Names of the partition schemas CreateSchemaAdapterAsync created. Dropped by
    // CleanDataAsync together with their data sources (#977): nothing else ever removed
    // them, so every partition a test left behind stayed in the container for the rest of
    // the run and every LATER test paid for it — see MaxTablesPerCleanupBatch.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _trackedSchemas = new();

    /// <summary>
    /// Schemas <see cref="InitializeAsync"/> provisions once per container (mirroring the prod
    /// migration) plus the catalog schemas. A test may legitimately build an adapter over one of
    /// them; dropping it would take the whole container's framework state with it, so they are
    /// never dropped even when tracked.
    /// </summary>
    private static readonly System.Collections.Immutable.ImmutableHashSet<string> UndroppableSchemas =
        System.Collections.Immutable.ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "public", "auth", "system_access", "information_schema");

    /// <summary>
    /// Upper bound on the number of <c>(schema, table)</c> DELETEs <see cref="CleanDataAsync"/>
    /// puts into ONE transaction — the fix for #977.
    ///
    /// <para>Npgsql sends a multi-statement command as a single implicit transaction, so the old
    /// "one batched DELETE over every pair in the container" held a lock on every targeted table
    /// AND every one of its indexes simultaneously (<c>mesh_nodes</c> alone carries 11 indexes →
    /// ~12 locks per pair). PostgreSQL's lock table is a fixed shared-memory array of
    /// <c>max_locks_per_transaction × (max_connections + max_prepared_transactions)</c> slots —
    /// 6400 at the container's defaults — so the cost of cleanup grew with every partition schema
    /// any earlier test left behind until it tipped over into
    /// <c>Npgsql.PostgresException 53200: out of shared memory</c>. The victim was whichever test
    /// happened to run at the tipping point (observed: the four NodeAuthorshipPersistenceTests),
    /// which is why it read as an unrelated regression, and it only ever reproduced on CI because
    /// only a full-suite container accumulates that many schemas.</para>
    ///
    /// <para>Chunking makes the lock footprint per transaction a CONSTANT (~20 pairs × ~12 locks)
    /// instead of a function of accumulated schemas. Raising the Postgres bound instead would only
    /// move the tipping point.</para>
    /// </summary>
    public const int MaxTablesPerCleanupBatch = 20;

    /// <summary>
    /// Upper bound on schemas dropped per transaction. <c>DROP SCHEMA … CASCADE</c> takes an
    /// ACCESS EXCLUSIVE lock on every object it removes (~90 per partition schema: 10 tables plus
    /// their indexes), so the drop is chunked for exactly the reason the DELETEs are.
    /// </summary>
    public const int MaxSchemasPerDropBatch = 4;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("meshweaver_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // 🚨 Cap the SHARED base pool. Left at the Npgsql default (MaxPoolSize=100) it can
        // hoard the container's entire max_connections=100 budget and hold those connections
        // open for the default 300 s idle lifetime — leaving NOTHING for the per-schema pools
        // (MaxPoolSize=1 each), of which a fan-out test activates ~30 at once. base(100)+30 > 100
        // → "53300: sorry, too many clients already". Sequential tests whose read fan-out is
        // bounded by the per-adapter pg-read pool (IoPoolOptions.PostgresRead=16) never need a big
        // base pool. Cap it well below the budget and prune idle connections fast so they RETURN to
        // the server promptly between tests.
        var csb = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            MaxPoolSize = 20,
            ConnectionIdleLifetime = 15,
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
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
        // Create schema — and remember it, so CleanDataAsync can drop it again (#977).
        await using (var cmd = DataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{Quote(schemaName)}\""))
            await cmd.ExecuteNonQueryAsync(ct);
        _trackedSchemas.Add(schemaName);

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
        => IoPool.Unbounded.Invoke(async ct => { await CleanDataAsync(ct); return Unit.Default; });

    /// <summary>
    /// Cleans all data tables for test isolation.
    /// </summary>
    public async Task CleanDataAsync(CancellationToken ct = default)
    {
        // Release per-schema pool connections first so the DELETE statements
        // don't compete with leaked schema adapters. Async-dispose so the physical
        // connections actually return to the server (sync Dispose can leave them
        // pending → 53300: too many clients under the sharded run).
        await DisposeTrackedSchemaDataSourcesAsync();

        // …and drop the schemas those data sources belonged to. The fixture created them, so
        // the fixture removes them: this is where the #977 accumulation is stopped AT THE SOURCE
        // rather than paid for by every later test. It is safe at exactly this point because
        // DisposeTrackedSchemaDataSourcesAsync has already made every adapter handed out for
        // those schemas unusable — no test can be holding a live one across a CleanData.
        await DropTrackedSchemasAsync(ct);

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
        await cmd.ExecuteNonQueryAsync(ct);

        // Per-partition schemas (orga, orgb, testorg, …) carry their own mesh_nodes +
        // satellite tables that survive prior tests in the same collection (threads in
        // `orga.threads` would otherwise leak across tests and skew cross-schema UNION
        // counts). The previous shape ran ~10 information_schema existence-probes PER
        // schema PER test inside a DO block; since test partition schemas accumulate
        // through the collection (they are never dropped), cleanup was O(10 × schemas)
        // and every test got slower as the suite progressed — QuerySyntaxTests measured
        // 0.12s/test early vs 2.5s/test late (20×), which is most of the full suite's
        // wall-clock. Instead: ONE catalog query resolves all (schema, data-table) pairs,
        // then a batched DELETE. Same tables, same isolation, O(1) catalog probing.
        // DELETE on the tiny/empty tables is ~free — the per-schema probing was the cost.
        //
        // 🚨 #977: the batch is CHUNKED. One command per chunk = one implicit transaction per
        // chunk, so PostgreSQL releases the relation + index locks between chunks and the lock
        // footprint stops being a function of how many schemas the run has accumulated. See
        // MaxTablesPerCleanupBatch; CleanupLockFootprintTests measures both shapes.
        var targets = await DiscoverPerSchemaCleanupTargetsAsync(ct);
        foreach (var batch in BuildCleanupBatches(targets))
        {
            await using var del = DataSource.CreateCommand(batch);
            await del.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The per-partition data tables <see cref="CleanDataAsync"/> empties between tests.</summary>
    private static readonly System.Collections.Immutable.ImmutableArray<string> PerSchemaTables =
    [
        "mesh_nodes", "threads", "activities", "user_activities", "access",
        "annotations", "notifications", "code", "partition_objects",
        "user_effective_permissions"
    ];

    /// <summary>
    /// ONE catalog query resolving every <c>(schema, table)</c> pair the per-schema cleanup has to
    /// empty. Ordered so the batching in <see cref="BuildCleanupBatches"/> — and therefore the lock
    /// footprint it produces — is deterministic and measurable.
    /// </summary>
    public async Task<IReadOnlyList<(string Schema, string Table)>> DiscoverPerSchemaCleanupTargetsAsync(
        CancellationToken ct = default)
    {
        var targets = new List<(string Schema, string Table)>();
        await using var listTables = DataSource.CreateCommand(
            """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_name = ANY($1)
              AND table_schema NOT IN ('public', 'pg_catalog', 'information_schema', 'pg_toast')
              AND table_schema NOT LIKE 'pg\_%'
            ORDER BY table_schema, table_name
            """);
        listTables.Parameters.AddWithValue(PerSchemaTables.ToArray());
        await using var rdr = await listTables.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            targets.Add((rdr.GetString(0), rdr.GetString(1)));
        return targets;
    }

    /// <summary>
    /// Splits <paramref name="targets"/> into DELETE batches of at most
    /// <paramref name="maxTablesPerBatch"/> tables each. Each returned string is executed as its
    /// OWN command — i.e. its own implicit transaction — which is what bounds the lock count
    /// (#977). Pure and deterministic so a test can measure the footprint it produces.
    /// </summary>
    public static IReadOnlyList<string> BuildCleanupBatches(
        IReadOnlyList<(string Schema, string Table)> targets,
        int maxTablesPerBatch = MaxTablesPerCleanupBatch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTablesPerBatch, 1);
        var batches = new List<string>((targets.Count + maxTablesPerBatch - 1) / maxTablesPerBatch);
        for (var offset = 0; offset < targets.Count; offset += maxTablesPerBatch)
        {
            var end = Math.Min(offset + maxTablesPerBatch, targets.Count);
            var sb = new System.Text.StringBuilder((end - offset) * 48);
            for (var i = offset; i < end; i++)
                sb.Append("DELETE FROM \"").Append(Quote(targets[i].Schema))
                  .Append("\".\"").Append(Quote(targets[i].Table)).Append("\";\n");
            batches.Add(sb.ToString());
        }
        return batches;
    }

    /// <summary>
    /// Drops every schema <see cref="CreateSchemaAdapterAsync"/> created since the last call, and
    /// de-registers it from <c>public.searchable_schemas</c> so no cross-schema fan-out is left
    /// pointing at a schema that no longer exists.
    ///
    /// <para>This is the source-side half of the #977 fix: without it the container ends a full
    /// suite carrying every partition schema every test ever asked for, which is what made the
    /// cleanup cost — and the cross-schema UNION fan-outs — grow all run long.</para>
    /// </summary>
    public async Task DropTrackedSchemasAsync(CancellationToken ct = default)
    {
        var schemas = new SortedSet<string>(StringComparer.Ordinal);
        while (_trackedSchemas.TryTake(out var schema))
            if (!UndroppableSchemas.Contains(schema))
                schemas.Add(schema);
        if (schemas.Count == 0)
            return;

        // De-register FIRST: a searchable_schemas row naming a dropped schema would send the
        // cross-schema UNION into a missing relation. One tiny statement, one table, unbounded
        // only in parameter count.
        await using (var deregister = DataSource.CreateCommand(
            "DELETE FROM public.searchable_schemas WHERE schema_name = ANY($1)"))
        {
            deregister.Parameters.AddWithValue(schemas.ToArray());
            await deregister.ExecuteNonQueryAsync(ct);
        }

        foreach (var chunk in schemas.Chunk(MaxSchemasPerDropBatch))
        {
            var sb = new System.Text.StringBuilder(chunk.Length * 48);
            foreach (var schema in chunk)
                sb.Append("DROP SCHEMA IF EXISTS \"").Append(Quote(schema)).Append("\" CASCADE;\n");
            await using var drop = DataSource.CreateCommand(sb.ToString());
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Escapes a SQL identifier for use inside double quotes.</summary>
    private static string Quote(string identifier) => identifier.Replace("\"", "\"\"");
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
