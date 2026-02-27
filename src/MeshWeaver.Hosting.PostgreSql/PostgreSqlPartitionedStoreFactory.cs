using System.Text.RegularExpressions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Factory for creating per-partition PostgreSQL persistence stores.
/// Each partition gets its own PostgreSQL schema with isolated tables.
/// Uses per-partition NpgsqlDataSource with SearchPath set to the schema.
/// </summary>
public partial class PostgreSqlPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly NpgsqlDataSource _baseDataSource;
    private readonly PostgreSqlStorageOptions _options;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly AccessService? _accessService;
    private readonly string _baseConnectionString;

    public PostgreSqlPartitionedStoreFactory(
        NpgsqlDataSource baseDataSource,
        string baseConnectionString,
        PostgreSqlStorageOptions options,
        IDataChangeNotifier? changeNotifier = null,
        IEmbeddingProvider? embeddingProvider = null,
        AccessService? accessService = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _changeNotifier = changeNotifier;
        _embeddingProvider = embeddingProvider;
        _accessService = accessService;
    }

    public async Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        var schemaName = SanitizeSchemaName(firstSegment);

        // Ensure vector extension exists (must run in public schema context)
        await using var extCmd = _baseDataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector");
        await extCmd.ExecuteNonQueryAsync(ct);

        // Create the schema using the base data source
        await using var cmd = _baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\"");
        await cmd.ExecuteNonQueryAsync(ct);

        // Create a per-schema data source with SearchPath including public for extension types (e.g. vector)
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schemaName},public"
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        dataSourceBuilder.UseVector();
        var schemaDataSource = dataSourceBuilder.Build();

        // Initialize tables in the schema (they go into the schema via search_path)
        var schemaOptions = new PostgreSqlStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            VectorDimensions = _options.VectorDimensions,
            Schema = schemaName
        };
        await PostgreSqlSchemaInitializer.InitializeAsync(schemaDataSource, schemaOptions, ct);

        // Create the storage adapter using the schema-specific data source
        var adapter = new PostgreSqlStorageAdapter(schemaDataSource, _embeddingProvider);

        // Create persistence core and query provider
        var core = new InMemoryPersistenceService(adapter, _changeNotifier);
        var queryProvider = new PostgreSqlMeshQuery(adapter, _changeNotifier, _accessService);

        return new PartitionedStore(core, queryProvider);
    }

    public async Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
    {
        var partitions = new List<string>();

        // Find schemas that contain a mesh_nodes table
        await using var cmd = _baseDataSource.CreateCommand("""
            SELECT schema_name
            FROM information_schema.schemata s
            WHERE EXISTS (
                SELECT 1 FROM information_schema.tables t
                WHERE t.table_schema = s.schema_name
                  AND t.table_name = 'mesh_nodes'
            )
            AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
            ORDER BY s.schema_name
            """);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    /// <summary>
    /// Sanitizes a partition name into a valid PostgreSQL schema name.
    /// </summary>
    public static string SanitizeSchemaName(string segment)
    {
        // Lowercase and replace non-alphanumeric with underscore
        var sanitized = SchemaNameRegex().Replace(segment.ToLowerInvariant(), "_");
        // Ensure it doesn't start with a digit
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;
        return sanitized;
    }

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex SchemaNameRegex();
}
