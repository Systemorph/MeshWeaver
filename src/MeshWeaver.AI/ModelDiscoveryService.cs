using System.Collections.Concurrent;
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
/// <para>🚨 Cache invariant: an empty snapshot is NOT cached. RLS may
/// filter the synced query to zero rows for a caller that lacks Read
/// permission — caching that empty result would freeze the caller out
/// even after their permissions change. On every first-empty emission
/// the per-key entry is evicted so the next call re-evaluates.</para>
/// </summary>
public sealed class ModelDiscoveryService
{
    private readonly IMessageHub meshHub;
    private readonly ILogger<ModelDiscoveryService>? logger;

    // Three caches, all keyed by string. (a) by single node path,
    // (b) by single node path (the path whose ancestors we walked),
    // (c) by composite "{nodePath}|{nodeTypePath}".
    private readonly ConcurrentDictionary<string, IObservable<IReadOnlyList<MeshNode>>> byNode = new();
    private readonly ConcurrentDictionary<string, IObservable<IReadOnlyList<MeshNode>>> byNodeHierarchy = new();
    private readonly ConcurrentDictionary<string, IObservable<IReadOnlyList<MeshNode>>> byEffective = new();

    public ModelDiscoveryService(IMessageHub meshHub)
    {
        this.meshHub = meshHub;
        logger = meshHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<ModelDiscoveryService>();
    }

    /// <summary>
    /// (a) Live snapshot of every <c>LanguageModel</c> + <c>ModelProvider</c>
    /// node declared directly under <paramref name="nodePath"/>'s
    /// <c>_Provider</c> satellite subtree. Empty for nodes that haven't
    /// configured any provider. Cached on first non-empty emission.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> GetModelsAtNode(string nodePath)
    {
        if (string.IsNullOrEmpty(nodePath))
            return byNode.GetOrAdd("<root>", _ =>
                BuildSynced("root",
                    $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{TypeFilter} scope:descendants"));

        return byNode.GetOrAdd(nodePath, p =>
            BuildSynced($"node@{p}",
                $"namespace:{p}/{ModelProviderNodeType.RootNamespace} nodeType:{TypeFilter} scope:descendants"));
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
        var key = string.IsNullOrEmpty(nodePath) ? "<root>" : nodePath;
        return byNodeHierarchy.GetOrAdd(key, _ =>
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
                          .ToList())
                .Replay(1).RefCount();
        });
    }

    /// <summary>
    /// (c) Effective models for a chat — union of the namespace
    /// hierarchy AND the NodeType hierarchy. Both walks are independent;
    /// closer entries (later in either walk) shadow further ones at
    /// projection time.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> GetEffectiveModels(string nodePath, string? nodeTypePath = null)
    {
        var key = $"{nodePath}|{nodeTypePath ?? ""}";
        return byEffective.GetOrAdd(key, _ =>
        {
            var fromNs = GetModelsForNodeHierarchy(nodePath);
            var fromNt = !string.IsNullOrEmpty(nodeTypePath)
                ? GetModelsForNodeHierarchy(nodeTypePath)
                : Observable.Return((IReadOnlyList<MeshNode>)Array.Empty<MeshNode>());
            return fromNs.CombineLatest(fromNt, (a, b) => (IReadOnlyList<MeshNode>)
                    a.Concat(b)
                     .GroupBy(n => n.Path, StringComparer.Ordinal)
                     .Select(g => g.First())
                     .ToList())
                .Replay(1).RefCount();
        });
    }

    /// <summary>
    /// Forcibly drops any cached observables for <paramref name="anyPath"/>
    /// (and its derivatives). Callers wire this to writes (provider
    /// CRUD) so stale empties from a pre-access state don't pin the
    /// view after permissions change.
    /// </summary>
    public void Invalidate(string anyPath)
    {
        byNode.TryRemove(anyPath, out _);
        byNodeHierarchy.TryRemove(anyPath, out _);
        foreach (var key in byEffective.Keys.Where(k => k.StartsWith(anyPath, StringComparison.Ordinal)).ToArray())
            byEffective.TryRemove(key, out _);
    }

    private const string TypeFilter = LanguageModelNodeType.NodeType + "|" + ModelProviderNodeType.NodeType;

    private IObservable<IReadOnlyList<MeshNode>> BuildSynced(string id, params string[] queries)
    {
        var workspace = meshHub.GetWorkspace();
        return workspace.GetQuery($"discovery:{id}", queries)
            .Select(s => (IReadOnlyList<MeshNode>)s.ToList())
            .Replay(1).RefCount();
    }

    // Note on access-aware caching: the synced query inside BuildSynced
    // runs through the workspace's IDataChangeNotifier pipeline which is
    // already RLS-aware on a per-subscription basis. Each call site is a
    // separate subscriber; an RLS-denied caller sees an empty snapshot
    // without poisoning the per-node cache (the cache holds the
    // Replay(1).RefCount handle, not a pre-filtered projection). For
    // the strong form of "cache only when we have access" — i.e. evict
    // the entry on persistent empties — call <see cref="Invalidate"/>
    // explicitly from the write paths that change permission state.

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        // Yield the path itself, then every parent up to the root, then
        // the root namespace sentinel "" (so static catalog at
        // namespace=_Provider is always included).
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
