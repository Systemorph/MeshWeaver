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
    /// Reactive handle to a MeshNode at <paramref name="path"/>. Two cases only:
    /// <list type="number">
    ///   <item><description><b>Own hub</b> — when <paramref name="path"/> matches the hub's
    ///     address: returns the local <see cref="MeshNodeReference"/> stream.</description></item>
    ///   <item><description><b>Remote</b> — subscribes to the owning per-node hub via
    ///     <see cref="WorkspaceExtensions.GetRemoteStream{TReduced,TReference}"/> +
    ///     <see cref="MeshNodeReference"/>.</description></item>
    /// </list>
    /// The owning hub's <c>MeshDataSource</c> loads its MeshNode at init, so
    /// <c>GetStream(new MeshNodeReference())</c> on the owning side is always
    /// populated. If the node does not exist at <paramref name="path"/>, the
    /// per-node hub never activates and the remote subscription does not emit —
    /// callers should bound with <c>.Take(1).Timeout(...)</c> and treat absence
    /// of an emission as "not found".
    /// </summary>
    public static IObservable<MeshNode> GetMeshNodeStream(this IWorkspace workspace, string path)
    {
        if (string.Equals(workspace.Hub.Address.ToString(), path, StringComparison.Ordinal))
            return workspace.GetMeshNodeStream();

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference());
        return stream
            .Where(change => change.Value != null)
            .Select(change => change.Value!);
    }

    /// <summary>
    /// Updates the OWN MeshNode of <paramref name="workspace"/> by applying
    /// <paramref name="update"/> through the data source's MeshNode partition stream
    /// — the data source persister flushes to storage, subscribers receive the update
    /// via the workspace synchronization protocol, no <see cref="IMeshStorage"/>
    /// calls (persistence belongs in <c>MeshDataSource</c> initialization only).
    ///
    /// <para>
    /// <b>Own-hub only.</b> To update a MeshNode at a remote address, post a
    /// <see cref="DataChangeRequest"/> with the already-built target node:
    /// <code>
    /// hub.Post(new DataChangeRequest { Updates = [updated] },
    ///     o =&gt; o.WithTarget(new Address(updated.Path)));
    /// </code>
    /// The owning hub's data layer (registered by <c>AddData()</c>) handles it
    /// natively — no <c>GetRemoteStream</c> / <c>SubscribeRequest</c> round trip,
    /// works even when no per-node hub has been separately activated.
    /// </para>
    /// </summary>
    public static void UpdateMeshNode(this IWorkspace workspace,
        Func<MeshNode, MeshNode> update,
        string? nodePath = null)
    {

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
