using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.PostgreSql.Test")]

namespace MeshWeaver.Hosting;

/// <summary>
/// Mesh catalog implementation that uses IStorageAdapter for storage.
/// Configure with AddFileSystemPersistence() for file-backed storage
/// or AddInMemoryPersistence() for transient storage.
/// </summary>
internal sealed class MeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IStorageAdapter persistenceService,
    IEnumerable<IMeshQueryProvider> queryProviders,
    IEnumerable<IStaticNodeProvider> staticNodeProviders,
    ILogger<MeshCatalog>? logger = null)
    : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;
    internal IStorageAdapter Persistence { get; } = persistenceService;
    internal Address MeshAddress => hub.Address;
    private readonly Lazy<IMessageHub> _persistenceHub = new(() => hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!);
    private IMessageHub PersistenceHub => _persistenceHub.Value;
    private readonly MeshQuery meshQuery = new(queryProviders, hub);
    private readonly Lazy<INodeConfigurationResolver?> _configResolver = new(() => hub.ServiceProvider.GetService<INodeConfigurationResolver>());
    private INodeConfigurationResolver? ConfigResolver => _configResolver.Value;

    /// <summary>
    /// Internal node lookup used ONLY by the routing layer (<see cref="RoutingServiceBase"/>).
    /// Application code MUST NOT call this — use <c>hub.GetMeshNode(path)</c> /
    /// <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> instead.
    /// See <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
    ///
    /// <para><b>Single query through <see cref="IMeshQueryCore"/> — never
    /// <c>Persistence.Read</c> outside <c>AddMeshDataSource</c>.</b> The query
    /// layer is the single boss of "find a MeshNode by path"; the partition is
    /// extracted from the path's first segment and pushed down to the storage
    /// adapter as one Postgres <c>SELECT</c>, with the static-node provider
    /// participating in the same fan-out.</para>
    /// </summary>
    internal IObservable<MeshNode?> GetNodeForRouting(Address address)
    {
        var addressKey = address.Path;

        if (Configuration.Nodes.TryGetValue(addressKey, out var node))
        {
            if (node.HubConfiguration == null && ConfigResolver != null)
                return ConfigResolver.ResolveConfiguration(node)
                    .Select(n => (MeshNode?)n);
            return Observable.Return<MeshNode?>(node);
        }

        return ((IMeshQueryCore)meshQuery)
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{addressKey}"),
                hub.JsonSerializerOptions)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(c => (MeshNode?)c.Items.FirstOrDefault())
            .SelectMany<MeshNode?, MeshNode?>(persistenceNode =>
            {
                if (persistenceNode == null) return Observable.Return<MeshNode?>(null);
                if (ConfigResolver == null) return Observable.Return<MeshNode?>(persistenceNode);
                return ConfigResolver.ResolveConfiguration(persistenceNode)
                    .Select(n => (MeshNode?)n);
            });
    }

    // Internal HubNodePersistence helper — used by HandleCreateNodeRequest pipeline.
    private HubNodePersistence NodePersistence => new(hub, this);


    /// <summary>
    /// Creates a new node in Transient state without confirming it.
    /// Returns an observable emitting the saved node, or OnError on failure.
    /// Subscribe to drive — do not await.
    /// </summary>
    internal IObservable<MeshNode> CreateTransientNode(MeshNode node)
    {
        if (!string.IsNullOrEmpty(node.NodeType) && !Configuration.Nodes.ContainsKey(node.NodeType))
            return Observable.Throw<MeshNode>(
                new InvalidOperationException($"NodeType '{node.NodeType}' is not registered"));

        if (!ValidatePath(node))
            return Observable.Throw<MeshNode>(
                new InvalidOperationException($"Invalid path structure for node: {node.Path}"));

        var transientNode = node with { State = MeshNodeState.Transient };

        // Auto-set MainNode for satellite types: point to parent node, not self
        if (!string.IsNullOrEmpty(transientNode.NodeType)
            && !string.IsNullOrEmpty(transientNode.Namespace)
            && Configuration.IsSatelliteNodeType(transientNode.NodeType)
            && transientNode.MainNode == transientNode.Path)
        {
            transientNode = transientNode with { MainNode = transientNode.Namespace };
        }

        var resolvedObs = ConfigResolver != null
            ? ConfigResolver.ResolveConfiguration(transientNode)
            : Observable.Return(transientNode);

        return resolvedObs
            .SelectMany(resolved => Persistence.Write(resolved, hub.JsonSerializerOptions))
            .Do(saved => logger?.LogInformation("Created transient node at path {Path}", saved.Path));
    }

    private static bool ValidatePath(MeshNode node)
    {
        // Path validation rules:
        // 1. Id cannot be empty
        if (string.IsNullOrWhiteSpace(node.Id))
            return false;

        // 2. Id cannot contain invalid characters
        if (node.Id.Contains("..") || node.Id.StartsWith('/') || node.Id.EndsWith('/'))
            return false;

        // 3. Namespace segments should be valid
        if (!string.IsNullOrEmpty(node.Namespace))
        {
            var segments = node.Namespace.Split('/');
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Path-cache invalidation hook. Currently a no-op because MeshCatalog holds no cache;
    /// a downstream IPathResolver may keep one and subscribe to IDataChangeNotifier instead.
    /// Kept for binary compatibility with existing callers.
    /// </summary>
    public void InvalidatePathCache(string path) { }

    /// <inheritdoc />
    /// <summary>
    /// IPathResolver implementation — direct observable composition (no Task bridge).
    /// The path-resolution layer is one of two sanctioned places that touch persistence
    /// directly (the other is IStorageAdapter itself); see
    /// Doc/Architecture/CqrsAndContentAccess.md.
    /// </summary>
    public IObservable<AddressResolution?> ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return Observable.Return<AddressResolution?>(null);
        path = path.TrimStart('/');
        if (string.IsNullOrEmpty(path)) return Observable.Return<AddressResolution?>(null);
        return ResolvePathCore(path);
    }

    /// <summary>
    /// Resolves a path reactively. The chain stays in observable composition all the way
    /// down to the leaves; <c>Observable.FromAsync</c> bridges only the actual DB hits
    /// (<see cref="IStorageAdapter.FindBestPrefixMatchAsync"/>, <c>GetNodeAsync</c>,
    /// <c>GetChildrenAsync</c>) — that's the one place we hit the database. Per
    /// <c>Doc/Architecture/CqrsAndContentAccess.md</c>, the path-resolution layer is
    /// one of two sanctioned places that touch persistence directly.
    /// </summary>
    internal IObservable<AddressResolution?> ResolvePathCore(string path)
    {
        var segments = path.Split('/');

        // 1. Configuration match — pure in-memory, no I/O. Synchronous.
        var configMatch = Configuration.Nodes.Values
            .Where(node => !node.IsSatelliteType)
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

        if (configMatch.Node != null)
        {
            logger?.LogDebug("[RESOLVE-DIAG] {Path} → CONFIG match {NodePath}", path, configMatch.Node.Path);
            return ResolveFromConfigNode(configMatch.Node, segments);
        }

        // 2. Persistence walk — observable chain.
        return FindBestPersistenceMatch(segments)
            .Select(match =>
            {
                if (match.Node == null)
                {
                    logger?.LogDebug("[RESOLVE-DIAG] {Path} → NO MATCH", path);
                    return (AddressResolution?)null;
                }
                var matchedPath = match.Node.Path;
                var remainder = match.MatchedSegments < segments.Length
                    ? string.Join("/", segments.Skip(match.MatchedSegments))
                    : null;
                logger?.LogDebug("[RESOLVE-DIAG] {Path} → PERSISTENCE match prefix={Prefix} remainder={Remainder}",
                    path, matchedPath, remainder ?? "(null)");
                return new AddressResolution(matchedPath, remainder);
            });
    }

    private IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPersistenceMatch(string[] segments)
    {
        var fullPath = string.Join("/", segments);

        // Step 1: prefix-match query at the DB layer. IObservable end-to-end.
        return Persistence.FindBestPrefixMatch(fullPath, hub.JsonSerializerOptions)
            .SelectMany(prefix =>
            {
                if (prefix.Item1 != null)
                {
                    logger?.LogDebug("[BRANCH] {Path} → STEP1 prefix match {NodePath} segments={Seg}", fullPath, prefix.Item1.Path, prefix.Item2);
                    return Observable.Return<(MeshNode?, int)>((prefix.Item1, prefix.Item2));
                }

                // Step 2: walk deepest-to-shallowest via direct GetNodeAsync.
                // Build a serial chain (Concat) and short-circuit on first hit.
                return WalkSegmentsForNode(segments)
                    .SelectMany(found =>
                    {
                        if (found.Node != null)
                        {
                            logger?.LogDebug("[BRANCH] {Path} → STEP2 node match {NodePath} depth={Depth}", fullPath, found.Node.Path, found.Depth);
                            return Observable.Return<(MeshNode?, int)>((found.Node, found.Depth));
                        }

                        // Step 3: virtual-namespace lookup (children iteration).
                        return WalkSegmentsForVirtualNamespace(segments)
                            .SelectMany(virt =>
                            {
                                if (virt.Node != null)
                                {
                                    logger?.LogDebug("[BRANCH] {Path} → STEP3 virtual match {NodePath} depth={Depth}", fullPath, virt.Node.Path, virt.Depth);
                                    return Observable.Return<(MeshNode?, int)>((virt.Node, virt.Depth));
                                }

                                // Step 4: static node provider fallback (in-memory).
                                var staticNodes = staticNodeProviders
                                    .SelectMany(p => p.GetStaticNodes())
                                    .ToArray();
                                for (int depth = segments.Length; depth >= 1; depth--)
                                {
                                    var testPath = string.Join("/", segments.Take(depth));
                                    var staticNode = staticNodes.FirstOrDefault(n =>
                                        string.Equals(n.Path, testPath, StringComparison.OrdinalIgnoreCase));
                                    if (staticNode != null)
                                    {
                                        logger?.LogDebug("[BRANCH] {Path} → STEP4 static match {NodePath} depth={Depth}", fullPath, staticNode.Path, depth);
                                        return Observable.Return<(MeshNode?, int)>((staticNode, depth));
                                    }
                                }
                                logger?.LogDebug("[BRANCH] {Path} → ALL STEPS NULL", fullPath);
                                return Observable.Return<(MeshNode?, int)>((null, 0));
                            });
                    });
            });
    }

    private IObservable<(MeshNode? Node, int Depth)> WalkSegmentsForNode(string[] segments)
    {
        var probes = new List<IObservable<(MeshNode? Node, int Depth)>>();
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var d = depth;
            var testPath = string.Join("/", segments.Take(d));
            // ⬇ Native IObservable surface — no FromAsync bridging needed.
            probes.Add(Persistence.Read(testPath, hub.JsonSerializerOptions)
                .Select(n => ((MeshNode?)n, d)));
        }
        return Observable.Concat(probes)
            .Where(t => t.Item1 != null)
            .Take(1)
            .DefaultIfEmpty(((MeshNode?)null, 0));
    }

    private IObservable<(MeshNode? Node, int Depth)> WalkSegmentsForVirtualNamespace(string[] segments)
    {
        var probes = new List<IObservable<(MeshNode? Node, int Depth)>>();
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var d = depth;
            var testPath = string.Join("/", segments.Take(d));
            // Path-only existence probe via the query engine — `select:path` keeps
            // it light, never reads stale `Content`. Persistence.GetChildren was
            // deleted in the persistence-cull (2026-05-11).
            // Per-probe timeout: ObserveQuery's MergeProviderObservables waits for
            // every provider's Initial frame before firing the merged Initial, so
            // a single stuck provider hangs the whole path-resolution chain. Cap
            // each probe so we fall through to the next (or to the catalog null
            // result) instead of blocking the caller indefinitely.
            probes.Add(meshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{testPath} scope:children select:path"))
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<QueryResultChange<MeshNode>, TimeoutException>(_ =>
                    Observable.Return(new QueryResultChange<MeshNode> { Items = [] }))
                .Select(change =>
                {
                    if (change.Items.Count == 0)
                        return ((MeshNode?)null, 0);
                    var ns = d > 1 ? string.Join("/", segments.Take(d - 1)) : null;
                    var virt = new MeshNode(segments[d - 1], ns) { Name = segments[d - 1] };
                    return ((MeshNode?)virt, d);
                }));
        }
        return Observable.Concat(probes)
            .Where(t => t.Item1 != null)
            .Take(1)
            .DefaultIfEmpty(((MeshNode?)null, 0));
    }

    private IObservable<AddressResolution?> ResolveFromConfigNode(MeshNode matchedNode, string[] segments)
    {
        // Pre-compute the config-only fallback resolution: matchedNode + remaining
        // segments as Remainder. Used both as the SelectMany target when persistence
        // returns no deeper match, and as the Timeout fallback below.
        AddressResolution? configResolution() =>
            new AddressResolution(matchedNode.Path,
                segments.Length > matchedNode.Segments.Count
                    ? string.Join("/", segments.Skip(matchedNode.Segments.Count))
                    : null);

        // When path goes deeper than the config node, check persistence for a deeper match
        if (segments.Length > matchedNode.Segments.Count &&
            matchedNode.Segments.Count > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
            // Persistence walk is best-effort: if no deeper match exists (or the chain
            // stalls — see WalkSegmentsForVirtualNamespace), fall back to the config
            // resolution rather than blocking the live consumer.
            return FindBestPersistenceMatch(segments)
                .Select(match =>
                {
                    if (match.Node != null && match.MatchedSegments > matchedNode.Segments.Count)
                    {
                        var persistenceRemainder = match.MatchedSegments < segments.Length
                            ? string.Join("/", segments.Skip(match.MatchedSegments))
                            : null;
                        return new AddressResolution(match.Node.Path, persistenceRemainder);
                    }
                    return configResolution();
                })
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<AddressResolution?, TimeoutException>(_ =>
                {
                    logger?.LogWarning(
                        "[RESOLVE] Persistence walk timed out for {Path} — falling back to config match {ConfigPath}",
                        string.Join("/", segments), matchedNode.Path);
                    return Observable.Return(configResolution());
                });
        }

        return Observable.Return(configResolution());
    }

    private static int ScoreMatch(MeshNode node, string[] pathSegments)
    {
        var nodeSegments = node.Segments;

        // Score = number of matching segments from start
        // Must match ALL node segments to count
        if (nodeSegments.Count > pathSegments.Length)
            return 0;

        for (int i = 0; i < nodeSegments.Count; i++)
        {
            if (!nodeSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                return 0;
        }

        return nodeSegments.Count;
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> Query(string? parentPath, string? query = null, int? maxResults = null)
    {
        var queryParts = new List<string>
        {
            string.IsNullOrEmpty(parentPath) ? "namespace:" : $"namespace:{parentPath}"
        };
        if (!string.IsNullOrWhiteSpace(query))
            queryParts.Add(query);

        var request = new MeshQueryRequest { Query = string.Join(" ", queryParts), Limit = maxResults };
        return meshQuery.ObserveQuery<MeshNode>(request)
            .Select(c => (IReadOnlyList<MeshNode>)(maxResults is int max && c.Items.Count > max
                ? c.Items.Take(max).ToList()
                : c.Items));
    }
}
