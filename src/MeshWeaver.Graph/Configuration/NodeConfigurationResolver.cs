using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Resolves hub configuration via <see cref="NodeTypeEnrichmentHelpers.EnrichWithNodeType"/>.
/// Pure observable surface — no Task bridge.
/// </summary>
internal class NodeConfigurationResolver(
    IMessageHub hub,
    MeshConfiguration meshConfiguration,
    NodeTypeServiceHub serviceHub,
    ILogger<NodeConfigurationResolver> logger) : INodeConfigurationResolver
{
    private readonly IMeshNodeCompilationService? _compilationService =
        hub.ServiceProvider.GetService<IMeshNodeCompilationService>();

    public IObservable<MeshNode> ResolveConfiguration(MeshNode node)
        => NodeTypeEnrichmentHelpers.EnrichWithNodeType(
            serviceHub.Hub, meshConfiguration, _compilationService, node, logger);
}
