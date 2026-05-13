using MeshWeaver.Mesh;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Reactive source for the list of <see cref="CreatableTypeInfo"/> usable
/// at a given navigation context. Backed by <c>workspace.GetQuery</c>
/// (synced mesh node queries) per
/// <c>Doc/Architecture/SyncedMeshNodeQueries.md</c> — never a global
/// <c>nodeType:NodeType</c> scan, always namespace-bounded.
///
/// <para>Replaces the legacy <c>INodeTypeService.GetCreatableTypesAsync</c>
/// IAsyncEnumerable surface. Consumers (navigation UI, autocomplete) bind
/// to the observable directly; the first emission lands once every
/// contributing synced query has its initial set.</para>
/// </summary>
public interface ICreatableTypesProvider
{
    /// <summary>
    /// Live list of creatable types for <paramref name="nodePath"/> with
    /// <paramref name="parentNode"/> as the resolved navigation context.
    /// Pass <c>null</c> for the root path to get the bounded global set
    /// (static AddMeshNodes entries + <c>MeshConfiguration.GlobalCreatableTypes</c>).
    /// </summary>
    IObservable<IReadOnlyList<CreatableTypeInfo>> GetCreatableTypes(
        string? nodePath, MeshNode? parentNode);
}
