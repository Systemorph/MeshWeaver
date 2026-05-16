using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IHostedService"/> wrapper that:
/// <list type="number">
///   <item>Discovers every pre-existing user/org schema in Postgres (any
///     schema that has a <c>mesh_nodes</c> table, excluding the public /
///     framework schemas) and registers a <see cref="PartitionDefinition"/>
///     for it before any request can fault on a missing partition. The
///     pre-Stage-0 factory created these stores lazily on first touch;
///     the new <see cref="PostgreSqlPartitionStorageProvider"/> requires
///     pre-registration, and most user partitions are NOT surfaced as
///     <c>Admin/Partition/{userId}</c> MeshNodes (the per-user-partition
///     migration writes a <c>Source/{userId}</c> MeshDataSource discovery
///     record, not an Admin/Partition entry).</item>
///   <item>Then calls
///     <see cref="PostgreSqlPartitionStorageProvider.SubscribeToWorkspace"/>
///     so any subsequent <c>Admin/Partition/*</c> MeshNode additions
///     (newly-created orgs / system partitions) layer in on top.</item>
/// </list>
///
/// <para>Without step #1, a user signing into prod immediately fires
/// <c>rbuergi/_UserActivity/rbuergi</c> against an unregistered partition,
/// the provider's <see cref="PostgreSqlPartitionStorageProvider.Matches"/>
/// returns false, and the FE renders "Error loading area: No node found
/// at 'rbuergi'." every 45 s in a polling loop.</para>
/// </summary>
internal sealed class PostgreSqlPartitionSubscriptionHostedService : IHostedService
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly IMessageHub _meshHub;
    private readonly ILogger<PostgreSqlPartitionSubscriptionHostedService>? _logger;
    private IDisposable? _subscription;

    /// <summary>
    /// Schemas that the framework owns / pre-creates via
    /// <c>DefaultPartitionProvider</c> and the global-satellite registrations.
    /// They show up in <c>information_schema.schemata</c> too but already get
    /// registered with the correct table mappings via the
    /// <c>Admin/Partition/*</c> MeshNode stream; skip them here so we don't
    /// overwrite a more-specific definition with a generic
    /// <c>StandardTableMappings</c> shape.
    /// </summary>
    private static readonly HashSet<string> ReservedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "pg_catalog", "pg_toast", "public",
        "admin", "portal", "kernel", "doc",
        "system_access", "system_activity", "system_user_activity", "system_thread",
    };

    public PostgreSqlPartitionSubscriptionHostedService(
        PostgreSqlPartitionStorageProvider provider,
        IMessageHub meshHub,
        ILogger<PostgreSqlPartitionSubscriptionHostedService>? logger = null)
    {
        _provider = provider;
        _meshHub = meshHub;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Starting PostgreSqlPartitionStorageProvider: discovering existing schemas");

        // 1. Pre-register every existing mesh_nodes-bearing schema. The
        //    namespace mirrors the schema name (per-user partitions: the
        //    user's id == schema name; orgs: org name); the standard
        //    satellite table mappings give us _Access/_Activity/_Thread/etc.
        //    routing within the partition.
        var discovered = 0;
        try
        {
            await using var cmd = _provider.BaseDataSource.CreateCommand("""
                SELECT t.table_schema
                FROM information_schema.tables t
                WHERE t.table_name = 'mesh_nodes'
                  AND t.table_schema NOT LIKE '%_versions'
                ORDER BY t.table_schema
                """);
            await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await rdr.ReadAsync(cancellationToken))
            {
                var schema = rdr.GetString(0);
                if (ReservedSchemas.Contains(schema)) continue;

                // Namespace = schema name (per the V10 per-user partition
                // convention: route `{userId}/...` to schema `{userId}`).
                // Standard table mappings: _Access → access,
                // _Activity → activities, etc. — the satellite tables get
                // created by V10/V14/etc. migrations alongside mesh_nodes.
                _provider.RegisterPartition(new PartitionDefinition
                {
                    Namespace = schema,
                    DataSource = "default",
                    Schema = schema,
                    Table = "mesh_nodes",
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Versioned = true,
                });
                discovered++;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "PostgreSqlPartitionSubscriptionHostedService: schema discovery failed; "
                + "existing user/org partitions will not route until their Admin/Partition "
                + "MeshNodes get streamed in.");
        }
        _logger?.LogInformation(
            "PostgreSqlPartitionSubscriptionHostedService: pre-registered {Count} existing schemas",
            discovered);

        // 2. Start the live Admin/Partition/* subscription so additions at
        //    runtime (new org partition created by a user) get picked up.
        _subscription = _provider.SubscribeToWorkspace(_meshHub);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
