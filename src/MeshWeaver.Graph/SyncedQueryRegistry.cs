using System.Collections.Concurrent;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Hub-level registry of named synced mesh-queries — keyed by the query
/// <em>name</em> (id) the caller registered, NOT by type-source iteration.
/// Each entry maps <c>name → IObservable&lt;IEnumerable&lt;MeshNode&gt;&gt;</c>;
/// the observable comes from <see cref="SyncedQueryMeshNodes.StreamUpdates"/>
/// and is already <c>Replay(1).RefCount()</c>-cached by the base
/// <see cref="MeshWeaver.Data.VirtualTypeSource{T}"/>, so multiple subscribers
/// share a single upstream subscription per name.
///
/// <para>
/// Populated by <see cref="SyncedQueryDataSourceExtensions.WithMeshQuery"/>
/// at data-source registration time; consumed by
/// <see cref="SyncedQueryDataSourceExtensions.GetQuery"/>.
/// </para>
/// </summary>
public sealed class SyncedQueryRegistry
{
    private readonly ConcurrentDictionary<object, IObservable<IEnumerable<MeshNode>>> _byName
        = new();

    /// <summary>Registers <paramref name="observable"/> under <paramref name="name"/>.
    /// Idempotent: re-registering the same name keeps the first registration
    /// (multiple data sources with the same id is a configuration error).</summary>
    public void Register(object name, IObservable<IEnumerable<MeshNode>> observable)
        => _byName.TryAdd(name, observable);

    /// <summary>Returns the cached observable for <paramref name="name"/>, or
    /// <c>null</c> if no synced query is registered with that name.</summary>
    public IObservable<IEnumerable<MeshNode>>? Get(object name)
        => _byName.TryGetValue(name, out var obs) ? obs : null;
}
