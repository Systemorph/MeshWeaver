using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Writes a compile's forward dependency edges (<c>Doc/Architecture/DependencyGraph</c>) as ONE
/// node, <c>{NodeTypePath}/_Dependency/Forward</c>, replaced whole — so a recompile that drops an
/// edge drops it in the same write that adds the new ones, and no reader ever needs a query to
/// find which edges to delete.
///
/// <para>Framework infrastructure, written as System (the compile itself runs as System; the
/// edges describe the type, not anybody's request). <c>CreateOrUpdateNode</c> is the right verb:
/// the content is the complete set, never a fold over the previous one.</para>
/// </summary>
internal static class DependencyEdgeSetWriter
{
    /// <summary>The node that stores <paramref name="edges"/>.</summary>
    /// <param name="edges">The edge set, already stamped with the build it was read from.</param>
    internal static MeshNode NodeFor(DependencyEdgeSet edges) =>
        new(DependencyEdgeSetPaths.ForwardId, DependencyEdgeSetPaths.NamespaceFor(edges.Source))
        {
            Name = "Forward dependencies",
            NodeType = DependencyEdgeSetPaths.NodeType,
            State = MeshNodeState.Active,
            Content = edges,
        };

    /// <summary>
    /// Writes <paramref name="edges"/>. Cold. A failure is reported at Error naming the type and
    /// completes the observable: the compile it follows has already landed, and an edge set that
    /// did not land reads as "not extracted for this build" (its <see cref="DependencyEdgeSet.BuildMvid"/>
    /// is absent or older), never as "depends on nothing".
    /// </summary>
    /// <param name="hub">A hub that outlives the compile's own activation (the settle hub).</param>
    /// <param name="edges">The edge set to store.</param>
    /// <param name="logger">Diagnostics.</param>
    internal static IObservable<Unit> Write(IMessageHub hub, DependencyEdgeSet edges, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateOrUpdateNode(NodeFor(edges)))
            .Take(1)
            .Select(_ => Unit.Default)
            .Catch<Unit, Exception>(ex =>
            {
                logger?.LogError(ex,
                    "Writing the dependency edges of {Source} (build {BuildMvid}) failed — the "
                    + "dependency graph has no edge set for this build", edges.Source, edges.BuildMvid);
                return Observable.Empty<Unit>();
            });
    }
}
