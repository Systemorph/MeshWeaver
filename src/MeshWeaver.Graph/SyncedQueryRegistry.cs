using System.Collections.Concurrent;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Hub-level registry of named synced mesh-queries — keyed by the query
/// <em>name</em> (id) the caller registered, NOT by type-source iteration.
/// Each entry stores both the cached observable (used by readers via
/// <see cref="SyncedQueryDataSourceExtensions.GetQuery(MeshWeaver.Data.IWorkspace, object)"/>)
/// and the underlying <see cref="SyncedQueryMeshNodes"/> source (used by the
/// delete-handler to walk every synced query and push direct
/// <see cref="SyncedQueryMeshNodes.NotifyDeleted"/> events when a path drops
/// out of any owned set).
///
/// <para>
/// Populated by <see cref="SyncedQueryDataSourceExtensions.WithMeshQuery"/>
/// at data-source registration time and by the get-or-create
/// <see cref="SyncedQueryDataSourceExtensions.GetQuery(MeshWeaver.Data.IWorkspace, object, string[])"/>
/// overload at first read.
/// </para>
/// </summary>
public sealed class SyncedQueryRegistry
{
    private readonly ConcurrentDictionary<object, Entry> _byName = new();

    /// <summary>Registers <paramref name="source"/> + <paramref name="observable"/> under <paramref name="name"/>.
    /// Idempotent: re-registering the same name keeps the first registration
    /// (multiple data sources with the same id is a configuration error).</summary>
    public void Register(object name, SyncedQueryMeshNodes source, IObservable<IEnumerable<MeshNode>> observable)
        => _byName.TryAdd(name, new Entry(source, observable));

    /// <summary>Returns the cached observable for <paramref name="name"/>, or
    /// <c>null</c> if no synced query is registered with that name.</summary>
    public IObservable<IEnumerable<MeshNode>>? Get(object name)
        => _byName.TryGetValue(name, out var entry) ? entry.Stream : null;

    /// <summary>
    /// Enumerates every <see cref="SyncedQueryMeshNodes"/> source registered
    /// on this workspace. Used by the framework's delete handler to walk
    /// every synced query and push a direct
    /// <see cref="SyncedQueryMeshNodes.NotifyDeleted"/> for paths each query
    /// owns — a synchronous reliability path on top of the upstream
    /// change-notifier driven Removed event.
    /// </summary>
    public IEnumerable<SyncedQueryMeshNodes> Enumerate()
        => _byName.Values.Select(e => e.Source);

    private readonly record struct Entry(
        SyncedQueryMeshNodes Source,
        IObservable<IEnumerable<MeshNode>> Stream);
}
