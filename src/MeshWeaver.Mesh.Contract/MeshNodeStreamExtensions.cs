using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Reactive helpers for reading <see cref="MeshNode"/> content from workspaces.
/// Canonical replacement for the lagged
/// <c>QueryAsync&lt;MeshNode&gt;($"path:{path}").FirstOrDefaultAsync()</c> pattern.
/// </summary>
public static class MeshNodeStreamExtensions
{
    /// <summary>
    /// Reactive handle to the current hub's own MeshNode. No query index, no await,
    /// no staleness, live updates on content changes. Compose with <c>.Take(1)</c>
    /// for one-shot reads or keep subscribed for live views.
    /// </summary>
    public static IObservable<MeshNode> GetMeshNodeStream(this IWorkspace workspace)
    {
        var stream = workspace.GetStream(new MeshNodeReference())
            ?? throw new InvalidOperationException(
                "MeshNode stream is not available — the workspace has no MeshNodeReference reducer.");
        return stream
            .Where(change => change.Value != null)
            .Select(change => change.Value!);
    }

    /// <summary>
    /// Reactive handle to a MeshNode at <paramref name="path"/>. Dispatches in priority
    /// order:
    /// <list type="number">
    ///   <item><description><b>Own hub</b> — when <paramref name="path"/> matches the hub's
    ///     address: returns the local <see cref="MeshNodeReference"/> stream.</description></item>
    ///   <item><description><b>Local collection</b> — when the workspace's MeshNode
    ///     <c>InstanceCollection</c> already contains the node (the common case for
    ///     mesh-hub reads of any node loaded by <c>MeshDataSource</c> at init):
    ///     filter the local stream by path. No <c>SubscribeRequest</c> goes to a remote
    ///     hub.</description></item>
    ///   <item><description><b>Remote</b> — fall back to
    ///     <see cref="WorkspaceExtensions.GetRemoteStream{TReduced,TReference}"/> for
    ///     nodes hosted on a separate hub.</description></item>
    /// </list>
    /// Callers don't have to distinguish — just pass the path. Empty/no-match emits
    /// nothing (combine with <c>.Take(1).Timeout(...)</c> for a "not found within X"
    /// semantic).
    ///
    /// <para>
    /// <b>Why the local-collection step matters:</b> for nodes that exist in the mesh
    /// hub's collection but have no separately-activated per-node hub (test scenarios,
    /// many production paths), going straight to <c>GetRemoteStream</c> sends a
    /// <c>SubscribeRequest</c> to the per-node address. That hub has no
    /// <c>SubscribeRequest</c> handler, the synchronization protocol gets a
    /// <c>DeliveryFailure</c>, and the read returns null/error. Checking the local
    /// collection first sidesteps that entire failure mode.
    /// </para>
    /// </summary>
    public static IObservable<MeshNode> GetMeshNodeStream(this IWorkspace workspace, string path)
    {
        if (string.Equals(workspace.Hub.Address.ToString(), path, StringComparison.Ordinal))
            return workspace.GetMeshNodeStream();

        // Local collection — preferred for any mesh-hub-loaded node. Avoids the cross-hub
        // SubscribeRequest round trip and works even when no per-node hub exists.
        var localCollection = workspace.GetStream<MeshNode>();
        if (localCollection != null)
        {
            return localCollection
                .Select(nodes => nodes?.FirstOrDefault(n =>
                    string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
                .Where(n => n != null)
                .Select(n => n!);
        }

        // Remote — node lives at a separate hub address, subscribe via MeshNodeReference.
        var remote = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        return remote
            .Where(change => change.Value != null)
            .Select(change => change.Value!);
    }

    /// <summary>
    /// Updates a MeshNode via the workspace. Local hub: routes through the data source's
    /// MeshNode partition stream. Remote address: pushes a patch via
    /// <c>GetRemoteStream&lt;InstanceCollection, CollectionReference&gt;</c>. Either path uses
    /// the synchronization protocol — no <see cref="IMeshStorage"/> calls; persistence
    /// belongs in <c>MeshDataSource</c> initialization only.
    /// </summary>
    public static void UpdateMeshNode(this IWorkspace workspace,
        Func<MeshNode, MeshNode> update,
        Address? address = null, string? nodePath = null)
    {
        if (address != null && !address.Equals(workspace.Hub.Address))
        {
            // Remote: update via CollectionReference stream on the remote hub.
            var remoteStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
                address, new CollectionReference(nameof(MeshNode)));
            remoteStream?.Update(current =>
            {
                if (current == null) throw new InvalidOperationException("no state of mesh nodes");
                var nId = nodePath?.Split('/').Last();
                var node = (nId is null
                    ? current.Instances.Values.First()
                    : current.Instances.GetValueOrDefault(nId)) as MeshNode;
                if (node is null) throw new InvalidOperationException("State is not a mesh node.");
                var updated = update(node);
                return new ChangeItem<InstanceCollection>(
                    current.SetItem(updated.Id, updated),
                    remoteStream.StreamId, remoteStream.StreamId,
                    ChangeType.Patch, remoteStream.Hub.Version,
                    [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = node }]);
            });
            return;
        }

        // Local: write to the data source's MeshNode partition stream — same stream the
        // workspace reduces from, so updates propagate to all subscribers (and to persistence
        // via the data source's persister).
        var dataSource = workspace.DataContext.GetDataSourceForType(typeof(MeshNode));
        if (dataSource == null)
            throw new InvalidOperationException("No data source registered for MeshNode");
        var dsStream = dataSource.GetStreamForPartition(null)
            ?? throw new InvalidOperationException("No stream for MeshNode partition");

        dsStream.Update(state =>
        {
            var store = state ?? new EntityStore();
            var collection = store.Collections.GetValueOrDefault(nameof(MeshNode));
            if (collection is null)
                throw new InvalidOperationException(
                    $"MeshNode collection not found. Available: [{string.Join(", ", store.Collections.Keys)}]");

            var nodeId = nodePath?.Split('/').Last();
            var current = (nodeId is null
                ? collection.Instances.Values.FirstOrDefault()
                : collection.Instances.GetValueOrDefault(nodeId)) as MeshNode;
            if (current == null)
                throw new InvalidOperationException(
                    $"MeshNode '{nodePath}' not found. Available: [{string.Join(", ", collection.Instances.Keys.Select(k => k.ToString()))}]");

            var updated = update(current);
            var newStore = store.Update(nameof(MeshNode), c => c.Update(updated.Id, updated));
            return dsStream.ApplyChanges(new EntityStoreAndUpdates(newStore,
                [new EntityUpdate(nameof(MeshNode), updated.Id, updated) { OldValue = current }],
                dsStream.StreamId));
        }, ex =>
        {
            var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Mesh.UpdateMeshNode");
            logger?.LogError(ex, "UpdateMeshNode failed for {NodePath}", nodePath);
            return Task.CompletedTask;
        });
    }
}
