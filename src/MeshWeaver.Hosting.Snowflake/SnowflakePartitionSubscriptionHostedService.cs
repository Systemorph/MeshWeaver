using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Boot-time seeding hook — the Snowflake port of
/// <c>PostgreSqlPartitionSubscriptionHostedService</c>: ensures the Snowflake schemas for every
/// framework partition (the ones explicitly registered by an <see cref="IStaticNodeProvider"/>
/// — Admin, User, Portal, Kernel, system_access, etc.) exist before the first
/// request. Each schema's <see cref="PartitionDefinition"/> is also registered on
/// <see cref="SnowflakePartitionStorageProvider"/> so the router can resolve the
/// <c>_</c>-prefix global-satellite namespaces (whose schema name differs from the
/// lowercased namespace) to their real schema.
///
/// <para><b>No enumeration, no existence probe.</b> We do NOT scan
/// <c>INFORMATION_SCHEMA.SCHEMATA</c> for arbitrary user/org schemas. The router
/// maps a path's first segment to a schema synchronously; reads tolerate an absent
/// schema (the driver's "does not exist or not authorized" error → empty), so a partition
/// created on another silo becomes routable immediately without any invalidation
/// round-trip.</para>
///
/// <para>This service eagerly provisions the static framework partitions
/// (<c>Admin</c> / <c>Auth</c> / <c>_Access</c>) at startup via
/// <see cref="SnowflakePartitionStorageProvider.EnsureSchemaForPartitionAsync"/>. Runtime
/// partition creation (User / Space) is provisioned eagerly on create by
/// <c>OwnsPartitionProvisioningValidator</c> — the router never lazily creates a schema on
/// write (a write to an unprovisioned partition faults with "does not exist").</para>
/// </summary>
internal sealed class SnowflakePartitionSubscriptionHostedService : IHostedService
{
    private readonly SnowflakePartitionStorageProvider _provider;
    private readonly IEnumerable<IStaticNodeProvider> _staticProviders;
    private readonly ILogger<SnowflakePartitionSubscriptionHostedService>? _logger;

    /// <summary>Creates the seeding hook over the partition storage provider and the static node providers.</summary>
    /// <param name="provider">The Snowflake partition storage provider whose schemas are seeded and whose registered-partition map is populated.</param>
    /// <param name="staticProviders">The DI-registered static node providers advertising the framework partition roots.</param>
    /// <param name="logger">Optional logger for seeding diagnostics.</param>
    public SnowflakePartitionSubscriptionHostedService(
        SnowflakePartitionStorageProvider provider,
        IEnumerable<IStaticNodeProvider> staticProviders,
        ILogger<SnowflakePartitionSubscriptionHostedService>? logger = null)
    {
        _provider = provider;
        _staticProviders = staticProviders;
        _logger = logger;
    }

    /// <summary>
    /// Walks every <see cref="IStaticNodeProvider"/>, and for each advertised node whose
    /// <see cref="MeshNode.Content"/> is a <see cref="PartitionDefinition"/> provisions the
    /// backing schema (idempotent; also registers the definition on the provider so the
    /// router resolves <c>_</c>-prefix globals). A throwing provider or a failing partition
    /// is logged and skipped — one broken definition must not block the rest of boot.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seeded = 0;
        foreach (var provider in _staticProviders)
        {
            IEnumerable<MeshNode> nodes;
            try { nodes = provider.GetStaticNodes(); }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "SnowflakePartitionSubscriptionHostedService: static node provider {Provider} threw; skipping",
                    provider.GetType().Name);
                continue;
            }
            foreach (var node in nodes)
            {
                if (node.Content is not PartitionDefinition def) continue;
                if (string.IsNullOrEmpty(def.Namespace)) continue;
                try
                {
                    await _provider.EnsureSchemaForPartitionAsync(def, cancellationToken).ConfigureAwait(false);
                    seeded++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "SnowflakePartitionSubscriptionHostedService: failed to ensure schema for static partition {Namespace}; skipping",
                        def.Namespace);
                }
            }
        }
        _logger?.LogInformation(
            "SnowflakePartitionSubscriptionHostedService: seeded {Count} framework partitions; "
            + "user/org partitions are provisioned eagerly on create via EnsurePartitionProvisioned.",
            seeded);
    }

    /// <summary>No teardown — provisioning is idempotent, one-way boot work.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
