using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🅿️ Clears a deleted NodeType's parked compile failure from the
/// <see cref="NodeTypeCompileParkRegistry"/> — the deletion-side companion of the
/// release-request un-park in <c>NodeTypeCompilationHelpers.InstallReleaseRequestWatcher</c>.
///
/// <para>The park registry is keyed by NodeType PATH and lives in a mesh-scoped in-memory
/// singleton, so it outlives the node itself: without this handler, deleting a parked
/// (terminally failed) NodeType and recreating it at the same path started PARKED — the
/// fresh type's first-build kickoff flipped Pending straight into the compile watcher's
/// parked short-circuit and the recreated type never compiled (delete+recreate did not
/// heal; only a process restart cleared the registry). Runs in the owner process holding
/// the registry singleton — the delete pipeline resolves post-deletion handlers from the
/// same service tree the per-NodeType hubs use.</para>
/// </summary>
public sealed class NodeTypeUnparkPostDeletionHandler(
    NodeTypeCompileParkRegistry parkRegistry,
    ILogger<NodeTypeUnparkPostDeletionHandler>? logger = null) : INodePostDeletionHandler
{
    /// <inheritdoc />
    public string NodeType => MeshNode.NodeTypePath;

    /// <inheritdoc />
    public IObservable<Unit> Handle(MeshNode deletedNode, string? deletedBy)
    {
        var path = deletedNode.Path;
        if (!string.IsNullOrEmpty(path))
        {
            if (parkRegistry.IsParked(path))
                logger?.LogInformation(
                    "NodeType '{Path}' deleted by {User} while PARKED — clearing the parked compile " +
                    "failure so a recreate at the same path starts clean.",
                    path, deletedBy ?? "system");
            // Unpark also resets the failure/attempt budgets — idempotent when not parked.
            parkRegistry.Unpark(path);
        }
        return Observable.Return(Unit.Default);
    }
}
