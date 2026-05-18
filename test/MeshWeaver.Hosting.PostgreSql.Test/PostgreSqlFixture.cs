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

        StorageAdapter = new PostgreSqlStorageAdapter(DataSource);
        AccessControl = new PostgreSqlAccessControl(DataSource);
    }

    public async ValueTask DisposeAsync()
    {
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
        return (schemaDs, adapter);
    }

    /// <summary>
    /// Cleans all data tables for test isolation.
    /// </summary>
    public async Task CleanDataAsync()
    {
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
    }
}

/// <summary>
/// Isolated PostgreSQL container reserved for tests that observe the
/// change feed (pg_notify LISTEN/NOTIFY pipeline). The default shared
/// fixture is used by ~25 test classes; some of those write data to
/// partition schemas (acme/futur/contoso/…) and trigger pg_notify events
/// on the same DataSource a LISTEN-based test is subscribed to. The
/// listener then receives changes from those neighbour tests and
/// ObserveQuery's "scope" filter — which guards on namespace prefix —
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
