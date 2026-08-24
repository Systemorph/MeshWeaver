using System.Reflection;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Fluent partition-storage configuration on
/// <see cref="MeshBuilder"/>. Each call registers an
/// <see cref="IPartitionStorageProvider"/> on the top-level service
/// collection; <c>RoutingPersistenceServiceCore</c> picks them
/// up at startup and routes reads/writes whose first path segment
/// matches the partition <c>Namespace</c> through the registered
/// adapter.
///
/// <para>This is the supported wire-up path going forward. The legacy
/// <c>IStaticNodeProvider</c> registrations (which made
/// <see cref="MeshWeaver.Graph.MeshDataSource.WithMeshNodes"/> re-enter the
/// <c>IMessageHub</c> singleton factory and stack-overflow under
/// certain configurations) are being retired one provider at a time;
/// <c>AddDocumentation</c> is the first migration.</para>
///
/// <para><b>Why MeshBuilder, not MessageHubConfiguration.</b>
/// <c>RoutingPersistenceServiceCore</c> is a top-level
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
            // 🚨 Resolved from the provider, not constructed eagerly, so module-contributed
            // parsers reach the embedded content. The AI module registers the agent parser this
            // way; an eagerly-built provider would see only the built-ins, and every embedded
            // `.md` carrying `nodeType: Agent` would be parsed by the catch-all Markdown parser
            // into a plain Markdown node — no exception, no log, the agent simply gone.
            services.AddSingleton<IPartitionStorageProvider>(sp =>
                new EmbeddedResourcePartitionStorageProvider(
                    @namespace, assembly, resourcePrefix, description, contexts: contexts,
                    contributedParsers: sp.GetServices<IFileFormatParser>()));
            return services;
        });
        return builder;
    }
}
