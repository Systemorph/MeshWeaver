namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Resolves HubConfiguration for a MeshNode on-demand.
/// Checks: node.HubConfiguration → cached compilation → compile from sources.
/// Composes with DefaultNodeHubConfiguration.
/// Used by both MonolithRoutingService and MessageHubGrain.
/// </summary>
public interface IMeshNodeHubFactory
{
    /// <summary>
    /// Returns the node enriched with HubConfiguration (and AssemblyLocation if compiled).
    /// Triggers async compilation if the node type has source code.
    /// Composes with DefaultNodeHubConfiguration from MeshConfiguration.
    /// </summary>
    Task<MeshNode> ResolveHubConfigurationAsync(MeshNode node, CancellationToken ct = default);
}
