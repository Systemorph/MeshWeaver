using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Resolves URL paths to hub addresses by delegating to
/// <see cref="IMeshQueryCore.ObserveQuery{T}"/>. The query expresses
/// <i>"every path that is a prefix of the requested path"</i> via the
/// canonical idiom (see <c>Doc/DataMesh/QuerySyntax.md</c> → "Path
/// Resolution"):
///
/// <code>path:{a|b|c} sort:length(path)-desc limit:1</code>
///
/// where <c>a|b|c</c> is the requested path plus each ancestor. The
/// multi-value <c>path:</c> parses to <c>WHERE path IN (...)</c> on backends
/// that push it down; <see cref="Observable.Scan"/> over the change stream
/// maintains a path-keyed set so deletions of the current top fall back to
/// the next-deepest ancestor without re-querying.
///
/// <para><b>Backend behaviour</b>:
/// <list type="bullet">
///   <item><b>Postgres</b> — one indexed lookup per partition, server-side
///     sort + limit.</item>
///   <item><b>InMemory / FileSystem</b> — exact-path probes via
///     <c>StorageAdapterMeshQueryProvider</c>. FileSystem reads honour the
///     directory + <c>index.md</c> convention.</item>
///   <item><b>Static providers</b> (built-in roles, agents,
///     <c>AddMeshNodes</c> seed) — in-memory <c>StartsWith</c> filter.</item>
/// </list></para>
///
/// <para><b>No PathResolution-level cache.</b> <c>ObserveQuery</c> is live;
/// memoizing here either races change-feed propagation (stale-NULL after
/// CreateNode, repro:
/// <c>AddressResolutionTest.ResolvePath_ExactMatch_ReturnsNullRemainder</c>)
/// or duplicates the provider-level caching that already exists. Each call
/// triggers a fresh query — bounded cost, single round-trip on Postgres.
/// No <c>MeshConfiguration.Nodes</c> scan, no <c>IStorageAdapter</c>
/// reach-through, no partition-provider enumeration.</para>
/// </summary>
internal class PathResolutionService : IPathResolver
{
    private readonly IMessageHub _hub;
    private readonly IMeshQueryCore _queryCore;
    private readonly ILogger<PathResolutionService>? _logger;

    public PathResolutionService(
        IMessageHub hub,
        IMeshQueryCore queryCore,
        ILogger<PathResolutionService>? logger = null)
    {
        _hub = hub;
        _queryCore = queryCore;
        _logger = logger;
    }

    public IObservable<AddressResolution?> ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        var normalized = path.TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
            return Observable.Return<AddressResolution?>(null);

        return ResolveOnce(normalized)
            .DistinctUntilChanged(AddressResolutionEquality.Instance);
    }

    private IObservable<AddressResolution?> ResolveOnce(string path)
    {
        var segments = path.Split('/');
        var pathList = string.Join("|", Enumerable.Range(1, segments.Length)
            .Select(depth => string.Join("/", segments.Take(depth)))
            .Reverse());
        var request = MeshQueryRequest.FromQuery($"path:{pathList}");

        return _queryCore.ObserveQuery<MeshNode>(request, _hub.JsonSerializerOptions)
            .Scan(
                seed: ImmutableDictionary.Create<string, MeshNode>(StringComparer.OrdinalIgnoreCase),
                accumulator: (set, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset => change.Items
                        .ToImmutableDictionary(n => n.Path, n => n, StringComparer.OrdinalIgnoreCase),
                    QueryChangeType.Added or QueryChangeType.Updated =>
                        change.Items.Aggregate(set, (s, n) => s.SetItem(n.Path, n)),
                    QueryChangeType.Removed =>
                        change.Items.Aggregate(set, (s, n) => s.Remove(n.Path)),
                    _ => set
                })
            .Select<ImmutableDictionary<string, MeshNode>, AddressResolution?>(set =>
            {
                var best = set.Values.OrderByDescending(n => n.Path.Length).FirstOrDefault();
                if (best is null) return null;
                var matchedSegments = best.Path.Split('/').Length;
                return BuildResolution(best.Path, segments, matchedSegments, matchedNode: best);
            })
            // ObserveQuery returns Observable.Empty when no provider matches.
            // Without DefaultIfEmpty the chain dies silently on completion and
            // RoutingServiceBase's .Take(1) waits forever.
            .DefaultIfEmpty();
    }

    private static AddressResolution BuildResolution(
        string matchedPath, string[] requestedSegments, int matchedSegments, MeshNode? matchedNode)
    {
        var remainder = matchedSegments < requestedSegments.Length
            ? string.Join("/", requestedSegments.Skip(matchedSegments))
            : null;
        return new AddressResolution(matchedPath, remainder, matchedNode);
    }

    /// <summary>
    /// Resolution-equality for <see cref="Observable.DistinctUntilChanged"/>.
    /// Path-shape (Prefix + Remainder) is the cache key; the Node is metadata
    /// that can change without altering the route.
    /// </summary>
    private sealed class AddressResolutionEquality : IEqualityComparer<AddressResolution?>
    {
        public static readonly AddressResolutionEquality Instance = new();
        public bool Equals(AddressResolution? x, AddressResolution? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Prefix, y.Prefix, StringComparison.Ordinal)
                && string.Equals(x.Remainder ?? "", y.Remainder ?? "", StringComparison.Ordinal);
        }
        public int GetHashCode(AddressResolution? obj) =>
            obj is null ? 0 : HashCode.Combine(obj.Prefix, obj.Remainder ?? "");
    }
}
