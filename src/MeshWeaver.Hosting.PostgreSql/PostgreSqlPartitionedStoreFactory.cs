using System.Text.RegularExpressions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    private readonly IReadOnlyList<NodeTypePermission> _nodeTypePermissions;
    private readonly string _baseConnectionString;
    private readonly Action<NpgsqlDataSourceBuilder>? _configureDataSource;
    private readonly SemaphoreSlim _schemaInitLock = new(1, 1);
    private List<PartitionDefinition>? _partitionDefinitions;

    public PostgreSqlPartitionedStoreFactory(
        NpgsqlDataSource baseDataSource,
        string baseConnectionString,
        PostgreSqlStorageOptions options,
        IDataChangeNotifier? changeNotifier = null,
        IEmbeddingProvider? embeddingProvider = null,
        AccessService? accessService = null,
        IEnumerable<NodeTypePermission>? nodeTypePermissions = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        _baseDataSource = baseDataSource;
        _options = options;
        _changeNotifier = changeNotifier;
        _embeddingProvider = embeddingProvider;
        _accessService = accessService;
        _nodeTypePermissions = (nodeTypePermissions ?? []).ToList();
        _configureDataSource = configureDataSource;

        // Ensure SSL for Azure PostgreSQL
        if (baseConnectionString.Contains("database.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var csb = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SslMode = SslMode.Require
            };
            _baseConnectionString = csb.ConnectionString;
        }
        else
        {
            _baseConnectionString = baseConnectionString;
        }
    }

    private NpgsqlDataSource BuildDataSource(string connectionString, bool useVector = false)
    {
        // Limit pool size per partition to avoid "too many clients" errors
        // when fan-out queries hit all partitions in parallel.
        // ConnectionIdleLifetime: close idle connections after 30s so previous app instances
        // don't hold server slots when the Docker container persists across restarts.
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 3,
            ConnectionIdleLifetime = 30
        };
        var dsb = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        if (useVector)
            dsb.UseVector();
        _configureDataSource?.Invoke(dsb);
        return dsb.Build();
    }

    public async Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        // Serialize schema creation to avoid "tuple concurrently updated" errors
        // when multiple partitions initialize in parallel (CREATE EXTENSION, CREATE SCHEMA)
        await _schemaInitLock.WaitAsync(ct);
        try
        {
            return await CreateStoreInternalAsync(firstSegment, ct);
        }
        finally
        {
            _schemaInitLock.Release();
        }
    }

    private async Task<PartitionedStore> CreateStoreInternalAsync(string firstSegment, CancellationToken ct)
    {
        var schemaName = SanitizeSchemaName(firstSegment);
        var versionsSchemaName = schemaName + "_versions";

        // Ensure vector extension exists (must run in public schema context)
        await using var extCmd = _baseDataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector");
        await extCmd.ExecuteNonQueryAsync(ct);

        // Create the org schema and its versions schema
        await using (var cmd = _baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\""))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = _baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{versionsSchemaName}\""))
            await cmd.ExecuteNonQueryAsync(ct);

        // Create a per-schema data source with SearchPath including public for extension types (e.g. vector)
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schemaName},public"
        };
        var schemaDataSource = BuildDataSource(builder.ConnectionString, useVector: true);

        // Create a versions data source with SearchPath pointing to the versions schema
        var versionsConnBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{versionsSchemaName},public"
        };
        var versionsDataSource = BuildDataSource(versionsConnBuilder.ConnectionString);

        // Initialize mesh tables + cross-schema trigger + versions table
        var schemaOptions = new PostgreSqlStorageOptions
        {
            ConnectionString = builder.ConnectionString,
            VectorDimensions = _options.VectorDimensions,
            Schema = schemaName
        };
        await PostgreSqlSchemaInitializer.InitializeWithVersionsSchemaAsync(
            _baseDataSource, schemaDataSource, versionsDataSource,
            schemaOptions, versionsSchemaName, ct);

        // Sync node type permissions to the new schema
        if (_nodeTypePermissions.Count > 0)
        {
            var ac = new PostgreSqlAccessControl(schemaDataSource);
            await ac.SyncNodeTypePermissionsAsync(_nodeTypePermissions, ct);
        }

        // Look up the PartitionDefinition for this partition to enable satellite table routing.
        // If no pre-existing definition, create one with StandardTableMappings for content partitions.
        var partitionDef = _partitionDefinitions?.FirstOrDefault(
            p => p.Namespace.Equals(firstSegment, StringComparison.OrdinalIgnoreCase));
        if (partitionDef == null)
        {
            partitionDef = new PartitionDefinition
            {
                Namespace = firstSegment,
                Schema = schemaName,
                DataSource = "default",
                TableMappings = PartitionDefinition.StandardTableMappings,
            };
        }

        // Ensure satellite tables exist for this partition
        if (partitionDef.TableMappings is { Count: > 0 })
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                schemaDataSource, _options, partitionDef.TableMappings.Values, ct);
        }

        // Create the storage adapter with partition definition for satellite table routing
        var adapter = new PostgreSqlStorageAdapter(schemaDataSource, _embeddingProvider, partitionDef);

        // Create query provider — RoutingPersistenceServiceCore creates the persistence core internally
        var queryProvider = new PostgreSqlMeshQuery(adapter, _changeNotifier, _accessService);
        // Version query reads from the versions schema
        var versionQuery = new PostgreSqlVersionQuery(versionsDataSource);

        return new PartitionedStore(adapter, queryProvider, versionQuery);
    }

    /// <summary>
    /// Pre-creates schemas for default partitions (Admin, User, etc.) during DB initialization.
    /// Each PartitionDefinition's Schema (or sanitized Namespace) becomes a PostgreSQL schema.
    /// Satellite table mappings from <see cref="PartitionDefinition.TableMappings"/> are also created.
    /// </summary>
    public async Task InitializeDefaultPartitionsAsync(
        IEnumerable<PartitionDefinition> partitions, CancellationToken ct = default)
    {
        // Store partition definitions for satellite table routing in CreateStoreAsync
        var partitionList = partitions.ToList();
        _partitionDefinitions = partitionList;

        // Ensure vector extension exists first
        await using var extCmd = _baseDataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector");
        await extCmd.ExecuteNonQueryAsync(ct);

        foreach (var partition in partitionList)
        {
            var schemaName = partition.Schema ?? SanitizeSchemaName(partition.Namespace);

            // Create the main schema
            await using (var cmd = _baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\""))
                await cmd.ExecuteNonQueryAsync(ct);

            // Create per-schema data source
            var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                SearchPath = $"{schemaName},public"
            };
            var schemaDataSource = BuildDataSource(builder.ConnectionString, useVector: true);

            var schemaOptions = new PostgreSqlStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                VectorDimensions = _options.VectorDimensions,
                Schema = schemaName
            };

            if (partition.Versioned)
            {
                var versionsSchemaName = schemaName + "_versions";
                await using (var cmd = _baseDataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{versionsSchemaName}\""))
                    await cmd.ExecuteNonQueryAsync(ct);

                var versionsConnBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
                {
                    SearchPath = $"{versionsSchemaName},public"
                };
                var versionsDataSource = BuildDataSource(versionsConnBuilder.ConnectionString);

                await PostgreSqlSchemaInitializer.InitializeWithVersionsSchemaAsync(
                    _baseDataSource, schemaDataSource, versionsDataSource,
                    schemaOptions, versionsSchemaName, ct);

                await versionsDataSource.DisposeAsync();
            }
            else
            {
                // Unversioned: just create mesh tables without history/triggers
                await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(
                    schemaDataSource, schemaOptions, ct);
            }

            // Create satellite tables from TableMappings
            if (partition.TableMappings is { Count: > 0 })
            {
                await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                    schemaDataSource, _options, partition.TableMappings.Values, ct);
            }

            // Sync node type permissions
            if (_nodeTypePermissions.Count > 0)
            {
                var ac = new PostgreSqlAccessControl(schemaDataSource);
                await ac.SyncNodeTypePermissionsAsync(_nodeTypePermissions, ct);
            }

            await schemaDataSource.DisposeAsync();
        }
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
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
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
