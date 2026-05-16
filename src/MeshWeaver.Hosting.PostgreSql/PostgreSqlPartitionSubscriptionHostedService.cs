using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
    private readonly IEnumerable<IStaticNodeProvider> _staticProviders;
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
        IEnumerable<IStaticNodeProvider> staticProviders,
        ILogger<PostgreSqlPartitionSubscriptionHostedService>? logger = null)
    {
        _provider = provider;
        _meshHub = meshHub;
        _staticProviders = staticProviders;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation(
            "Starting PostgreSqlPartitionStorageProvider: seeding static partitions, discovering existing schemas");

        // 1. Synchronously seed every static-node-provider PartitionDefinition.
        //    This makes Admin/User/Portal/Kernel and the global satellites
        //    (_Access, _Activity, _UserActivity, _Thread) routable from the
        //    instant StartAsync returns — i.e., before any test or request
        //    code can call IMeshService.CreateNode and trip
        //    "no IPartitionStorageProvider matches". The workspace stream
        //    below still picks up runtime additions (new orgs etc.).
        var seeded = await SeedStaticPartitionsAsync(cancellationToken);
        _logger?.LogInformation(
            "PostgreSqlPartitionSubscriptionHostedService: seeded {Count} static partitions from IStaticNodeProvider",
            seeded);

        // 2. Discover pre-existing user/org schemas (V10 per-user partitions).
        var discovered = await DiscoverAndRegisterSchemasAsync(_provider, _logger, cancellationToken);
        _logger?.LogInformation(
            "PostgreSqlPartitionSubscriptionHostedService: pre-registered {Count} existing schemas",
            discovered);

        // 3. Live subscription for runtime additions (new org partition created
        //    by a user).
        _subscription = _provider.SubscribeToWorkspace(_meshHub);
    }

    /// <summary>
    /// Pulls <see cref="PartitionDefinition"/>s out of every registered
    /// <see cref="IStaticNodeProvider"/>, ensures their SQL schemas / tables
    /// exist, then registers them with the storage provider. Same data the
    /// <c>Admin/Partition/*</c> workspace stream would emit, but available
    /// synchronously at hosted-service startup so no consumer races the
    /// initial emission.
    /// </summary>
    private async Task<int> SeedStaticPartitionsAsync(CancellationToken ct)
    {
        var seeded = 0;
        foreach (var provider in _staticProviders)
        {
            IEnumerable<MeshNode> nodes;
            try { nodes = provider.GetStaticNodes(); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "PostgreSqlPartitionSubscriptionHostedService: static node provider {Provider} threw; skipping",
                    provider.GetType().Name);
                continue;
            }
            foreach (var node in nodes)
            {
                if (node.Content is not PartitionDefinition def) continue;
                if (string.IsNullOrEmpty(def.Namespace)) continue;
                try
                {
                    await _provider.EnsureSchemaForPartitionAsync(def, ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "PostgreSqlPartitionSubscriptionHostedService: failed to ensure schema for static partition {Namespace}; skipping",
                        def.Namespace);
                    continue;
                }
                _provider.RegisterPartition(def);
                seeded++;
            }
        }
        return seeded;
    }

    /// <summary>
    /// Pre-register every existing mesh_nodes-bearing schema. The namespace
    /// mirrors the schema name (per-user partitions: the user's id == schema
    /// name; orgs: org name); the standard satellite table mappings give us
    /// _Access/_Activity/_Thread/etc. routing within the partition.
    /// Exposed internally so the regression test can drive it without
    /// having to wire up an IMessageHub for step 2 (live subscription).
    /// </summary>
    internal static async Task<int> DiscoverAndRegisterSchemasAsync(
        PostgreSqlPartitionStorageProvider provider,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var discovered = 0;
        try
        {
            await using var cmd = provider.BaseDataSource.CreateCommand("""
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

                provider.RegisterPartition(new PartitionDefinition
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
            logger?.LogError(ex,
                "PostgreSqlPartitionSubscriptionHostedService: schema discovery failed; "
                + "existing user/org partitions will not route until their Admin/Partition "
                + "MeshNodes get streamed in.");
        }
        return discovered;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
