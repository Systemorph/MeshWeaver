using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Wires <see cref="IMeshQueryProvider.ObserveQuery{T}"/> into a
/// <see cref="VirtualDataSource"/> so a hub gets a live, synced collection
/// populated from a mesh query.
/// <para>
/// The Initial change populates the workspace; subsequent Added / Updated /
/// Removed deltas fold into the same store via the existing
/// <c>VirtualDataSource</c> stream-update plumbing. Code inside the hub then
/// reads the collection via <c>workspace.GetStream(new CollectionReference(name))</c>
/// like any other registered type — no Observe round-trip, no CQRS lag, no
/// awaits on hub-reachable code (Doc/Architecture/AsynchronousCalls.md).
/// </para>
/// <para>
/// Use case: per-NodeType hubs synchronously expose <c>Sources</c> /
/// <c>Tests</c> Code-node collections; per-data-node hubs synchronously
/// expose <c>AccessAssignments</c>; etc. The same pattern serves any
/// hub-local view onto the wider mesh.
/// </para>
/// </summary>
public static class SyncedQueryDataSourceExtensions
{
    /// <summary>
    /// Adds a virtual type to <paramref name="ds"/> whose contents are the
    /// live result set of <paramref name="query"/>. The collection name in
    /// the workspace is <paramref name="collectionName"/> (defaults to the
    /// type name).
    /// </summary>
    public static VirtualDataSource WithMeshQuery<T>(
        this VirtualDataSource ds,
        string query,
        string? collectionName = null) where T : class
    {
        return ds.WithVirtualType<T>(
            workspace =>
            {
                var provider = workspace.Hub.ServiceProvider
                    .GetRequiredService<IMeshQueryProvider>();
                return provider
                    .ObserveQuery<T>(
                        MeshQueryRequest.FromQuery(query),
                        workspace.Hub.JsonSerializerOptions)
                    .Select(change => (IEnumerable<T>)change.Items);
            },
            collectionName);
    }
}
