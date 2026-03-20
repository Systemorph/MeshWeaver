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

        // Create per-schema data source
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            SearchPath = $"{schemaName},public"
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dsBuilder.UseVector();
        var schemaDs = dsBuilder.Build();

        // Initialize mesh_nodes table in the schema
        await PostgreSqlSchemaInitializer.InitializeAsync(schemaDs, Options);

        // Create satellite tables if partition definition has mappings
        if (partitionDef?.TableMappings is { Count: > 0 })
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                schemaDs, Options, partitionDef.TableMappings.Values, ct);
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

[CollectionDefinition("PostgreSql")]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;
