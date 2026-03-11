using System.Threading.Tasks;
using MeshWeaver.Hosting.PostgreSql;
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

        // Initialize schema
        var options = new PostgreSqlStorageOptions();
        await PostgreSqlSchemaInitializer.InitializeAsync(DataSource, options);

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
