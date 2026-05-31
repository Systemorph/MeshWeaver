using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Canonical, shared synced-query helper for a NodeType's source+test
/// MeshNode set. Every consumer that needs the live source collection —
/// the per-NodeType hub's sources/IsDirty watcher
/// (<c>NodeTypeCompilationHelpers.InstallSourcesWatcher</c>), the compile
/// activity (<c>NodeTypeCompileActivityHandler</c>), the Configuration
/// layout-area listing (<c>NodeTypeLayoutAreas.Configuration</c>) — MUST go
/// through this helper so they share the same <c>workspace.GetQuery</c>
/// id and therefore the same <c>Replay(1).RefCount()</c> upstream
/// subscription.
///
/// <para>The cache key is the NodeType path. Two calls with the same
/// <c>nodeTypePath</c> on the same <c>workspace</c>
/// return the same underlying observable; a third caller doesn't open a
/// fresh <c>SubscribeRequest</c>, doesn't re-walk the query providers.
/// Mirrors the <see cref="MeshWeaver.Mesh.Services.IMeshNodeStreamCache"/>
/// pattern at the collection level.</para>
///
/// <para>Definition-aware: <see cref="NodeTypeDefinition.Sources"/> and
/// <see cref="NodeTypeDefinition.Tests"/> are resolved via
/// <see cref="CodeQueryResolver"/>, so the helper expands the same query
/// strings the compiler executes — the "what compiles" set and the
/// "what's dirty?" set are guaranteed identical.</para>
/// </summary>
public static class NodeSources
{
    /// <summary>
    /// Returns the cached, live <see cref="IObservable{T}"/> of source +
    /// test <see cref="MeshNode"/>s for a NodeType. Every emission carries
    /// the COMPLETE current set, deduplicated by path — rebuild your view
    /// from each emission rather than diffing.
    /// </summary>
    /// <param name="workspace">The hub whose workspace caches the synced
    /// query. Pass the per-NodeType hub's workspace at sites that run on
    /// that hub; the activity hub's workspace at sites that run there.</param>
    /// <param name="def">The NodeType's content; its
    /// <see cref="NodeTypeDefinition.Sources"/> and
    /// <see cref="NodeTypeDefinition.Tests"/> drive query expansion. <c>null</c>
    /// uses <see cref="CodeQueryResolver.DefaultSources"/> +
    /// <see cref="CodeQueryResolver.DefaultTests"/>.</param>
    /// <param name="nodeTypePath">Used both for cache-key uniqueness and
    /// for <c>$self</c> expansion inside the resolver.</param>
    public static IObservable<IReadOnlyList<MeshNode>> GetSources(
        IWorkspace workspace,
        NodeTypeDefinition? def,
        string nodeTypePath)
    {
        var queries = CodeQueryResolver
            .ExpandAll(def?.Sources, CodeQueryResolver.DefaultSources, nodeTypePath)
            .Concat(CodeQueryResolver.ExpandAll(def?.Tests, CodeQueryResolver.DefaultTests, nodeTypePath))
            .ToArray();

        if (queries.Length == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        var id = CacheId(nodeTypePath);
        return workspace.GetQuery(id, queries)
            .Select(items => (IReadOnlyList<MeshNode>)items.ToList());
    }

    /// <summary>
    /// Canonical <c>GetQuery</c> id for a
    /// NodeType's source set. Exposed so consumers that want to interact
    /// with the cache directly (e.g. tests asserting cache reuse) can
    /// reference the exact same key.
    /// </summary>
    public static string CacheId(string nodeTypePath) =>
        $"nodetype-sources:{nodeTypePath}";
}
