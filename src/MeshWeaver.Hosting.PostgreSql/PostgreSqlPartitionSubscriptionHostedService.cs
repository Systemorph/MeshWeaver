using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Boot-time seeding hook: ensures the Postgres schemas for every framework
/// partition (the ones explicitly registered by an <see cref="IStaticNodeProvider"/>
/// — Admin, User, Portal, Kernel, system_access, etc.) exist before the first
/// request. Each schema's <see cref="PartitionDefinition"/> is also registered on
/// <see cref="PostgreSqlPartitionStorageProvider"/> so the router can resolve the
/// <c>_</c>-prefix global-satellite namespaces (whose schema name differs from the
/// lowercased namespace) to their real schema.
///
/// <para><b>No enumeration, no existence probe.</b> We do NOT scan
/// <c>information_schema.schemata</c> for arbitrary user/org schemas. The router
/// maps a path's first segment to a schema synchronously; reads tolerate an absent
/// schema (Postgres <c>42P01</c> → empty), so a partition created on another silo
/// becomes routable immediately without any invalidation round-trip.</para>
///
/// <para>This service is the only place that calls
/// <see cref="PostgreSqlPartitionStorageProvider.EnsureSchemaForPartitionAsync"/>
/// at startup — runtime partition creation flows through the normal write
/// chain (which calls EnsureSchemaForPartitionSync on first write).</para>
/// </summary>
internal sealed class PostgreSqlPartitionSubscriptionHostedService : IHostedService
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly IEnumerable<IStaticNodeProvider> _staticProviders;
    private readonly ILogger<PostgreSqlPartitionSubscriptionHostedService>? _logger;

    public PostgreSqlPartitionSubscriptionHostedService(
        PostgreSqlPartitionStorageProvider provider,
        IEnumerable<IStaticNodeProvider> staticProviders,
        ILogger<PostgreSqlPartitionSubscriptionHostedService>? logger = null)
    {
        _provider = provider;
        _staticProviders = staticProviders;
        _logger = logger;
    }

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
                    await _provider.EnsureSchemaForPartitionAsync(def, cancellationToken).ConfigureAwait(false);
                    seeded++;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "PostgreSqlPartitionSubscriptionHostedService: failed to ensure schema for static partition {Namespace}; skipping",
                        def.Namespace);
                }
            }
        }
        _logger?.LogInformation(
            "PostgreSqlPartitionSubscriptionHostedService: seeded {Count} framework partitions; "
            + "user/org partitions resolve lazily via PgPartitionCache.",
            seeded);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
