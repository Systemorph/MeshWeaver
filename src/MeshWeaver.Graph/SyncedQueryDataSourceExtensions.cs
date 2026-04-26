using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Wires <see cref="IMeshQueryProvider.ObserveQuery{T}"/> + per-node remote
/// streams into a hub's data context as a synced collection of
/// <see cref="MeshNode"/>s. Two pieces are registered together:
///
/// <list type="bullet">
///   <item>A <see cref="VirtualDataSource"/> hosting a
///     <see cref="SyncedQueryMeshNodes"/> typesource (the read side — combined
///     per-node remote streams driven by the live query result set).</item>
///   <item>A workspace-level reducer that resolves
///     <c>MeshNodeReference(path)</c> to the cached per-node remote stream when
///     <paramref name="path"/> is in this source's result set. <c>.Update(...)</c>
///     on the resulting stream propagates through the synchronization protocol
///     to the owning per-node hub. The reducer returns null for paths outside
///     the source's set so a sibling synced source (e.g. a different query)
///     gets a chance — first match wins.</item>
/// </list>
/// </summary>
public static class SyncedQueryDataSourceExtensions
{
    /// <summary>
    /// Registers a synced <see cref="MeshNode"/> collection on this data context.
    /// </summary>
    /// <param name="data">The data context.</param>
    /// <param name="id">Unique data-source id (used as the persistence partition).</param>
    /// <param name="query">Mesh query string (see Query Syntax docs); must select <see cref="MeshNode"/>s.</param>
    /// <param name="collectionName">Workspace collection name; defaults to <c>nameof(MeshNode)</c>.</param>
    public static DataContext AddSyncedQuery(
        this DataContext data,
        object id,
        string query,
        string? collectionName = null)
    {
        // Capture the collection name we'll register under so the reducer can
        // look up THIS specific synced source (multiple synced sources on the
        // same hub coexist as separate workspace collections — e.g., "Red" /
        // "Green" — even though they share the MeshNode CLR type).
        var sourceCollection = collectionName ?? nameof(MeshNode);

        return data
            .WithVirtualDataSource(id, vs =>
            {
                var typeSource = new SyncedQueryMeshNodes(vs.Workspace, vs.Id, query, collectionName);
                return vs.WithTypeSource(typeof(MeshNode), typeSource);
            })
            .Configure(rm => rm.AddWorkspaceReferenceStream<MeshNode>((workspace, reference, configCb) =>
            {
                if (reference is not MeshNodeReference { Path: { } path }
                    || string.IsNullOrEmpty(path))
                    return null;

                // Resolve THIS source by its registered collection name; the
                // reducer is registered per-source so each source's reducer
                // only fires for its own paths.
                if (workspace.DataContext.GetTypeSource(sourceCollection) is not SyncedQueryMeshNodes typeSource)
                    return null;
                if (!typeSource.Owns(path))
                    return null;

                // The workspace's per-(addr, ref) cache means this is the same
                // stream the synced read side is subscribed to — read + write
                // share one upstream subscription per node.
                return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                    new Address(path), new MeshNodeReference());
            }));
    }

    /// <summary>
    /// Convenience overload: chain on a <see cref="VirtualDataSource"/> the way
    /// older callers expect. The reducer registration happens inside
    /// <see cref="AddSyncedQuery"/>; this overload preserves the
    /// <c>WithVirtualDataSource(... vs => vs.WithMeshQuery(...))</c> pattern by
    /// simply adding the synced typesource to <paramref name="ds"/>. Callers
    /// that need the reducer registered too should prefer
    /// <see cref="AddSyncedQuery"/>.
    /// </summary>
    public static VirtualDataSource WithMeshQuery(
        this VirtualDataSource ds,
        string query,
        string? collectionName = null)
    {
        var typeSource = new SyncedQueryMeshNodes(ds.Workspace, ds.Id, query, collectionName);
        return ds.WithTypeSource(typeof(MeshNode), typeSource);
    }
}
