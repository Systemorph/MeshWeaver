using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Hierarchical model + provider discovery anchored to the top-level
/// mesh hub. Mirrors the shape of the access-rights resolver — both
/// the node-path hierarchy AND the NodeType-path hierarchy contribute,
/// combined by closest-wins.
///
/// <para>🚨 Registered on the MESH hub (the long-lived top-level hub),
/// NEVER on a per-thread / per-execution hub. Per-thread hubs can be
/// blocked by an in-flight handler (see <c>feedback_synced_query_thread_hub.md</c>);
/// anchoring the synced subscriptions here means the cache survives
/// any thread-execution stall.</para>
///
/// <para>Three layers, all backed by <c>workspace.GetQuery</c>:
/// <list type="number">
///   <item><b>(a)</b> <see cref="GetModelsAtNode"/> — exact-node
///         snapshot. One synced query per node path.</item>
///   <item><b>(b)</b> <see cref="GetModelsForNodeHierarchy"/> — walks
///         UP the path. Combines (a) for the node + parent + grandparent
///         + … + root. Most levels emit empty.</item>
///   <item><b>(c)</b> <see cref="GetEffectiveModels"/> — union of (b)
///         applied to the node-path AND (b) applied to the NodeType-path.
///         This is what the chat-client factory / picker actually asks
///         for: "what models are available at this thread, given its
///         current path and its NodeType's path"?</item>
/// </list>
/// </para>
///
/// <para>No materialised observable cache: every call rebuilds the
/// composition over <see cref="BuildSynced"/>, whose underlying
/// <c>workspace.GetQuery(id, …)</c> is itself cached by id
/// (<c>Replay(1).RefCount()</c> upstream). Rebuilding the
/// <c>CombineLatest</c> wrapper is cheap and always reflects live state —
/// no stale empties to evict, so no <c>Invalidate</c> needed. RLS is
/// applied per-subscription at the source: a caller without Read sees an
/// empty snapshot without affecting any other caller.</para>
/// </summary>
public sealed class ModelDiscoveryService
{
    private readonly IMessageHub meshHub;
    private readonly ILogger<ModelDiscoveryService>? logger;

    /// <summary>
    /// Creates the discovery service anchored to the supplied mesh hub.
    /// </summary>
    /// <param name="meshHub">The long-lived top-level mesh hub whose workspace backs every synced
    /// discovery query; its service provider also supplies the optional logger.</param>
    public ModelDiscoveryService(IMessageHub meshHub)
    {
        this.meshHub = meshHub;
        logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<ModelDiscoveryService>();
    }

    /// <summary>
    /// (a) Live snapshot of every <c>LanguageModel</c> + <c>ModelProvider</c>
    /// node declared directly under <paramref name="nodePath"/>'s
    /// <c>Provider</c> satellite subtree. Empty for nodes that haven't
    /// configured any provider.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> GetModelsAtNode(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath))
            return BuildSynced("root",
                $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{TypeFilter} scope:descendants");

        return BuildSynced($"node@{nodePath}",
            $"namespace:{nodePath}/{ModelProviderNodeType.RootNamespace} nodeType:{TypeFilter} scope:descendants");
    }

    /// <summary>
    /// (b) Walks UP the path hierarchy (node → parent → grandparent → …
    /// → root), combining every level's
    /// <see cref="GetModelsAtNode"/> emission. Most levels will emit
    /// empty; the union is what the caller subscribes to. Closest-wins
    /// merging is the projector's job — this method just hands back the
    /// full union sorted by depth.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> GetModelsForNodeHierarchy(string nodePath)
    {
        var streams = EnumerateAncestors(nodePath)
            .Select(GetModelsAtNode)
            .ToArray();
        if (streams.Length == 0)
            return Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>());
        return Observable.CombineLatest(streams)
            .Select(levels => (IReadOnlyList<MeshNode>)
                levels.SelectMany(l => l)
                      .GroupBy(n => n.Path, StringComparer.Ordinal)
                      .Select(g => g.First())
                      .ToList());
    }

    /// <summary>
    /// (c) Effective models for a chat — union of the namespace
    /// hierarchy AND the NodeType hierarchy. Both walks are independent;
    /// closer entries (later in either walk) shadow further ones at
    /// projection time.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> GetEffectiveModels(string nodePath, string? nodeTypePath = null)
    {
        var fromNs = GetModelsForNodeHierarchy(nodePath);
        var fromNt = !string.IsNullOrEmpty(nodeTypePath)
            ? GetModelsForNodeHierarchy(nodeTypePath)
            : Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>());
        return fromNs.CombineLatest(fromNt, (a, b) => (IReadOnlyList<MeshNode>)
            a.Concat(b)
             .GroupBy(n => n.Path, StringComparer.Ordinal)
             .Select(g => g.First())
             .ToList());
    }

    private const string TypeFilter = LanguageModelNodeType.NodeType + "|" + ModelProviderNodeType.NodeType;

    private IObservable<IReadOnlyList<MeshNode>> BuildSynced(string id, params string[] queries)
    {
        var workspace = meshHub.GetWorkspace();
        // workspace.GetQuery is cached by id (Replay(1).RefCount upstream), so
        // re-projecting per call is cheap and always reflects live state.
        return workspace.GetQuery($"discovery:{id}", queries)
            .Select(s => (IReadOnlyList<MeshNode>)s.ToList());
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        // Yield the path itself, then every parent up to the root, then
        // the root namespace sentinel "" (so the static catalog at
        // namespace=Provider is always included).
        if (!string.IsNullOrEmpty(path))
        {
            yield return path;
            var current = path;
            while (true)
            {
                var idx = current.LastIndexOf('/');
                if (idx <= 0) break;
                current = current[..idx];
                yield return current;
            }
        }
        yield return "";
    }
}
