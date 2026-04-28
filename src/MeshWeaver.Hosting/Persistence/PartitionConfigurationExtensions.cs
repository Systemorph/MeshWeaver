using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Fluent partition-storage configuration on
/// <see cref="MeshBuilder"/>. Each call registers an
/// <see cref="IPartitionStorageProvider"/> on the top-level service
/// collection; <see cref="RoutingPersistenceServiceCore"/> picks them
/// up at startup and routes reads/writes whose first path segment
/// matches the partition <c>Namespace</c> through the registered
/// adapter.
///
/// <para>This is the supported wire-up path going forward. The legacy
/// <c>IStaticNodeProvider</c> registrations (which made
/// <see cref="MeshDataSource.WithMeshNodes"/> re-enter the
/// <c>IMessageHub</c> singleton factory and stack-overflow under
/// certain configurations) are being retired one provider at a time;
/// <c>AddDocumentation</c> is the first migration.</para>
///
/// <para><b>Why MeshBuilder, not MessageHubConfiguration.</b>
/// <see cref="RoutingPersistenceServiceCore"/> is a top-level
/// singleton; per-hub <c>WithServices</c> registrations are scoped to
/// the per-hub container and are invisible to it. Registrations have
/// to land on the <see cref="MeshBuilder"/>'s services so the routing
/// core can enumerate them at activation. The shape is still fluent
/// — <c>mesh.AddEmbeddedResourcePartition(...)</c> reads the same as
/// the per-hub config builder.</para>
/// </summary>
public static class PartitionConfigurationExtensions
{
    /// <summary>
    /// Registers a read-only embedded-resource partition. The first
    /// path segment of every node served by this partition is
    /// <paramref name="namespace"/>; resource names are matched on
    /// <paramref name="resourcePrefix"/> and converted to paths by
    /// replacing dots with slashes (last dot is the file extension).
    /// </summary>
    public static TBuilder AddEmbeddedResourcePartition<TBuilder>(
        this TBuilder builder,
        string @namespace,
        Assembly assembly,
        string resourcePrefix,
        string? description = null,
        IEnumerable<string>? contexts = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IPartitionStorageProvider>(
                new EmbeddedResourcePartitionStorageProvider(
                    @namespace, assembly, resourcePrefix, description, contexts: contexts));
            return services;
        });
        return builder;
    }
}
