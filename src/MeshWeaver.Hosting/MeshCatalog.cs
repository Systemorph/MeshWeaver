using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// Mesh catalog implementation that uses IMeshStorage for storage.
/// Configure with AddFileSystemPersistence() for file-backed storage
/// or AddInMemoryPersistence() for transient storage.
/// </summary>
internal sealed class MeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IMeshStorage persistenceService,
    IEnumerable<IMeshQueryProvider> queryProviders,
    IEnumerable<IStaticNodeProvider> staticNodeProviders,
    ILogger<MeshCatalog>? logger = null)
    : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;
    internal IMeshStorage Persistence { get; } = persistenceService;
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
    /// </summary>
    internal IObservable<MeshNode?> GetNodeForRouting(Address address)
    {
        var addressKey = address.Path;

        if (Configuration.Nodes.TryGetValue(addressKey, out var node))
        {
            if (node.HubConfiguration == null && ConfigResolver != null)
                return Observable.FromAsync(ct => ConfigResolver.ResolveConfigurationAsync(node, ct))
                    .Select(n => (MeshNode?)n);
            return Observable.Return<MeshNode?>(node);
        }

        return Observable.FromAsync(() => Persistence.GetNodeAsync(addressKey))
            .Select(persistenceNode =>
                persistenceNode ?? staticNodeProviders
                    .SelectMany(p => p.GetStaticNodes())
                    .FirstOrDefault(n => string.Equals(n.Path, addressKey, StringComparison.OrdinalIgnoreCase)))
            .SelectMany<MeshNode?, MeshNode?>(persistenceNode =>
            {
                if (persistenceNode == null) return Observable.Return<MeshNode?>(null);
                if (ConfigResolver == null) return Observable.Return<MeshNode?>(persistenceNode);
                return Observable.FromAsync(ct => ConfigResolver.ResolveConfigurationAsync(persistenceNode, ct))
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
            ? Observable.FromAsync(ct => ConfigResolver.ResolveConfigurationAsync(transientNode, ct))
            : Observable.Return(transientNode);

        return resolvedObs
            .SelectMany(resolved => Persistence.SaveNode(resolved))
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
    /// IPathResolver implementation — bridges the internal Task-based core via
    /// Observable.FromAsync. The path-resolution layer is one of two sanctioned places
    /// that touch persistence directly (the other is IMeshStorage itself); see
    /// Doc/Architecture/CqrsAndContentAccess.md.
    /// </summary>
    public IObservable<AddressResolution?> ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return Observable.Return<AddressResolution?>(null);
        path = path.TrimStart('/');
        if (string.IsNullOrEmpty(path)) return Observable.Return<AddressResolution?>(null);
        return Observable.FromAsync(() => ResolvePathCoreAsync(path));
    }

    /// <summary>
    /// Resolves a path reactively. The chain stays in observable composition all the way
    /// down to the leaves; <c>Observable.FromAsync</c> bridges only the actual DB hits
    /// (<see cref="IMeshStorage.FindBestPrefixMatchAsync"/>, <c>GetNodeAsync</c>,
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
            return ResolveFromConfigNode(configMatch.Node, segments);

        // 2. Persistence walk — observable chain.
        return FindBestPersistenceMatch(segments)
            .Select(match =>
            {
                if (match.Node == null)
                {
                    logger?.LogDebug("ResolvePath: no match found for path={Path}", path);
                    return (AddressResolution?)null;
                }
                var matchedPath = match.Node.Path;
                var remainder = match.MatchedSegments < segments.Length
                    ? string.Join("/", segments.Skip(match.MatchedSegments))
                    : null;
                return new AddressResolution(matchedPath, remainder);
            });
    }

    // Internal Task wrapper retained for the IPathResolver bridge until callers fully migrate.
    internal Task<AddressResolution?> ResolvePathCoreAsync(string path)
        => ResolvePathCore(path).FirstAsync().ToTask();

    private IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPersistenceMatch(string[] segments)
    {
        var fullPath = string.Join("/", segments);

        // Step 1: prefix-match query at the DB layer. ⬇ FromAsync only here, at the leaf.
        return Observable.FromAsync(() => Persistence.FindBestPrefixMatchAsync(fullPath))
            .SelectMany(prefix =>
            {
                if (prefix.Item1 != null)
                {
                    logger?.LogDebug("FindBestPersistenceMatch: prefix match {Path}", prefix.Item1.Path);
                    return Observable.Return<(MeshNode?, int)>((prefix.Item1, prefix.Item2));
                }

                // Step 2: walk deepest-to-shallowest via direct GetNodeAsync.
                // Build a serial chain (Concat) and short-circuit on first hit.
                return WalkSegmentsForNode(segments)
                    .SelectMany(found =>
                    {
                        if (found.Node != null)
                        {
                            logger?.LogDebug("FindBestPersistenceMatch: node {Path}", found.Node.Path);
                            return Observable.Return<(MeshNode?, int)>((found.Node, found.Depth));
                        }

                        // Step 3: virtual-namespace lookup (children iteration).
                        return WalkSegmentsForVirtualNamespace(segments)
                            .SelectMany(virt =>
                            {
                                if (virt.Node != null)
                                    return Observable.Return<(MeshNode?, int)>((virt.Node, virt.Depth));

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
                                        return Observable.Return<(MeshNode?, int)>((staticNode, depth));
                                }
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
            // ⬇ FromAsync only here — single DB hit per probe.
            probes.Add(Observable.FromAsync(() => Persistence.GetNodeAsync(testPath))
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
            probes.Add(Observable.FromAsync(async () =>
            {
                // ⬇ FromAsync wraps the DB hit; first child existence check.
                await using var enumerator = Persistence.GetChildrenAsync(testPath).GetAsyncEnumerator();
                if (await enumerator.MoveNextAsync())
                {
                    var ns = d > 1 ? string.Join("/", segments.Take(d - 1)) : null;
                    var virt = new MeshNode(segments[d - 1], ns) { Name = segments[d - 1] };
                    return ((MeshNode?)virt, d);
                }
                return ((MeshNode?)null, 0);
            }));
        }
        return Observable.Concat(probes)
            .Where(t => t.Item1 != null)
            .Take(1)
            .DefaultIfEmpty(((MeshNode?)null, 0));
    }

    private IObservable<AddressResolution?> ResolveFromConfigNode(MeshNode matchedNode, string[] segments)
    {
        // When path goes deeper than the config node, check persistence for a deeper match
        if (segments.Length > matchedNode.Segments.Count &&
            matchedNode.Segments.Count > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
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
                    var remainder = segments.Length > matchedNode.Segments.Count
                        ? string.Join("/", segments.Skip(matchedNode.Segments.Count))
                        : null;
                    return new AddressResolution(matchedNode.Path, remainder);
                });
        }

        var simpleRemainder = segments.Length > matchedNode.Segments.Count
            ? string.Join("/", segments.Skip(matchedNode.Segments.Count))
            : null;
        return Observable.Return<AddressResolution?>(new AddressResolution(matchedNode.Path, simpleRemainder));
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
    public async IAsyncEnumerable<MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = 0;

        // Build query string for IMeshQuery
        var queryParts = new List<string>();
        queryParts.Add(string.IsNullOrEmpty(parentPath) ? "namespace:" : $"namespace:{parentPath}");
        if (!string.IsNullOrWhiteSpace(query))
            queryParts.Add(query);

        var fullQuery = string.Join(" ", queryParts);

        {
            var request = new MeshQueryRequest { Query = fullQuery, Limit = maxResults };
            await foreach (var item in meshQuery.QueryAsync(request, ct))
            {
                if (item is MeshNode child)
                {
                    yield return child;
                    count++;

                    // Apply max results if provided
                    if (maxResults.HasValue && maxResults.Value > 0 && count >= maxResults.Value)
                    {
                        yield break;
                    }
                }
            }
        }
    }
}
