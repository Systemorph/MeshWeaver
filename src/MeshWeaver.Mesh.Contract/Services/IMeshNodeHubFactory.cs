namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Resolves <see cref="MeshNode.HubConfiguration"/> for a MeshNode on-demand.
/// Checks: <c>node.HubConfiguration</c> → cached compilation → compile from sources.
/// Composes with <c>DefaultNodeHubConfiguration</c>.
/// Used by both <c>MonolithRoutingService</c> and <c>MessageHubGrain</c>.
///
/// <para><b>Reactive surface — no Task</b>. The previous Task-returning shape
/// (<c>ResolveHubConfigurationAsync</c>) deadlocked under load: callers
/// <c>await</c>'d it from inside a hub action block / grain scheduler, the
/// internal compile chain posted messages back through the same routing
/// service, and the action block was already blocked waiting for those
/// messages to land. The reactive surface composes with
/// <c>nodeTypeService.EnrichWithNodeType(...)</c> (also <see cref="IObservable{T}"/>)
/// without capturing the caller's synchronization context, so a hub-flow
/// caller can <c>Subscribe(onNext, onError)</c> and let the chain run on
/// the producer's scheduler. Boundaries that genuinely require a Task
/// (Orleans grain activation, ASP.NET request hooks) bridge once at the
/// edge with <c>.FirstAsync().ToTask(ct)</c> per AGENTS.md.
/// </para>
/// </summary>
public interface IMeshNodeHubFactory
{
    /// <summary>
    /// Emits the node enriched with <see cref="MeshNode.HubConfiguration"/>
    /// (and, for dynamic NodeTypes, the persisted assembly reference fields on
    /// <c>NodeTypeDefinition.LatestAssemblyCollection</c> /
    /// <c>LatestAssemblyPath</c>). Triggers the reactive compilation chain when
    /// the node type has source code; composes the resulting per-node hub
    /// configuration with the mesh's <c>DefaultNodeHubConfiguration</c> so
    /// per-node hubs inherit cross-cutting concerns (security pipeline, layout
    /// areas, etc.) registered globally.
    /// </summary>
    IObservable<MeshNode> ResolveHubConfiguration(MeshNode node);
}
