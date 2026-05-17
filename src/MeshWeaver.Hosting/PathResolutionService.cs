using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Resolves URL paths to hub addresses with one PG query per path and a
/// <see cref="System.Reactive.Linq.Observable.Replay{T}(IObservable{T}, int)"/>
/// (1).<see cref="System.Reactive.Linq.Observable.RefCount{T}(System.Reactive.Subjects.IConnectableObservable{T})"/>
/// cache per path so concurrent subscribers share a single resolution stream.
///
/// <para><b>Source of truth</b>: <see cref="IStorageAdapter.ResolvePath"/> —
/// one round-trip per resolve, longest-prefix match across primary + satellite
/// tables (see <c>PathResolutionTests</c>). The pre-existing 4-step walk
/// (catalog STEP1..STEP4) is replaced by:
/// <list type="number">
///   <item>Configuration match — sync, in-memory.</item>
///   <item>Storage adapter ResolvePath — one query.</item>
///   <item>Static-node-provider exact-path fallback — sync, in-memory.</item>
///   <item>Partition-root fallback — sync, in-memory, via
///     <see cref="IPartitionStorageProvider.Matches"/>. Lets <c>/rbuergi</c>
///     resolve when the user's partition has no MeshNode at the bare path
///     (content lives in satellites).</item>
/// </list></para>
///
/// <para><b>Cache</b>: <c>ConcurrentDictionary&lt;path, Observable&gt;</c>
/// where each entry is <c>BuildLiveStream(path).Replay(1).RefCount()</c>.
/// Hot while any subscriber; auto-evicts on idle. Created/Deleted
/// notifications remove the cached entry (next subscriber re-resolves with
/// the new state) AND broadcast on <c>_catalogChanges</c> so existing
/// subscribers re-emit immediately.</para>
///
/// <para><b>Always up-to-date</b>: the change-feed pipe is authoritative.
/// New subscribers see the cached last value via Replay(1); the catalogChanges
/// Subject drives re-resolves on every relevant Created/Deleted. For
/// long-lived subscribers this is invisible; for one-shot <c>.Take(1)</c>
/// reads there's a small staleness window only if the change event hasn't
/// landed yet.</para>
/// </summary>
internal class PathResolutionService : IPathResolver, IDisposable
{
    private readonly IMessageHub _hub;
    private readonly IStorageAdapter _storageAdapter;
    private readonly MeshConfiguration _configuration;
    private readonly IStaticNodeProvider[] _staticNodeProviders;
    private readonly IPartitionStorageProvider[] _partitionStorageProviders;
    private readonly IDisposable? _createSub;
    private readonly IDisposable? _deleteSub;
    private readonly ILogger<PathResolutionService>? _logger;

    /// <summary>
    /// Live-shared resolution streams keyed by path. Each entry is a hot
    /// <c>Replay(1).RefCount()</c> observable — first subscriber kicks off
    /// the initial resolve; subsequent subscribers get the cached value
    /// instantly AND any re-emits driven by catalog changes.
    /// </summary>
    private readonly ConcurrentDictionary<string, IObservable<AddressResolution?>> _streams =
        new(StringComparer.OrdinalIgnoreCase);

    // Fan-out for catalog change notifications. OnCreated / OnDeleted push
    // the event onto this subject; every active resolution stream watches it
    // filtered by MightAffect.
    private readonly Subject<MeshChangeEvent> _catalogChanges = new();

    public PathResolutionService(
        IMessageHub hub,
        IStorageAdapter storageAdapter,
        MeshConfiguration configuration,
        IEnumerable<IStaticNodeProvider> staticNodeProviders,
        IEnumerable<IPartitionStorageProvider> partitionStorageProviders,
        IMeshChangeFeed? changeFeed = null,
        ILogger<PathResolutionService>? logger = null)
    {
        _hub = hub;
        _storageAdapter = storageAdapter;
        _configuration = configuration;
        _staticNodeProviders = staticNodeProviders.ToArray();
        _partitionStorageProviders = partitionStorageProviders.ToArray();
        _logger = logger;

        _createSub = changeFeed?.Subscribe(OnCreated, MeshChangeKind.Created);
        _deleteSub = changeFeed?.Subscribe(OnDeleted, MeshChangeKind.Deleted);
    }

    public IObservable<AddressResolution?> ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        var normalized = path.TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
            return Observable.Return<AddressResolution?>(null);

        return _streams.GetOrAdd(normalized, BuildCachedStream);
    }

    private IObservable<AddressResolution?> BuildCachedStream(string path) =>
        BuildLiveStream(path)
            .Replay(1)
            .RefCount();

    private IObservable<AddressResolution?> BuildLiveStream(string path) =>
        _catalogChanges
            .Where(e => MightAffect(path, e.Path))
            .Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .SelectMany(_ => ResolveOnce(path))
            .DistinctUntilChanged(AddressResolutionEquality.Instance);

    /// <summary>
    /// One-shot resolution: configuration → storage → static → partition root.
    /// Each step is short-circuit; the storage step is the only DB round-trip.
    /// The matched <see cref="MeshNode"/> is carried back on
    /// <see cref="AddressResolution.Node"/> so routing can share this cached
    /// observable instead of issuing a second <c>path:X</c> query.
    /// </summary>
    private IObservable<AddressResolution?> ResolveOnce(string path)
    {
        var segments = path.Split('/');

        // 1. Configuration match — pure in-memory, no I/O.
        var configMatch = _configuration.Nodes.Values
            .Where(node => !node.IsSatelliteType)
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();
        if (configMatch.Node != null)
            return Observable.Return<AddressResolution?>(
                BuildResolution(configMatch.Node.Path, segments, MatchedSegments(configMatch.Node.Path),
                    matchedNode: configMatch.Node));

        // 2. Storage adapter — one PG query covering primary + satellites.
        return _storageAdapter.ResolvePath(path, _hub.JsonSerializerOptions)
            .Select<(MeshNode? Node, int MatchedSegments), AddressResolution?>(result =>
            {
                if (result.Node != null)
                    return BuildResolution(result.Node.Path, segments, result.MatchedSegments, matchedNode: result.Node);

                // 3. Static-node-provider exact-path fallback.
                var staticHit = ProbeStaticNodes(segments);
                if (staticHit is { } sh && sh.Node is not null)
                    return BuildResolution(sh.Node.Path, segments, sh.Depth, matchedNode: sh.Node);

                // 4. Partition-root fallback — when the first segment maps
                //    to a registered partition but no MeshNode exists at
                //    that exact path (e.g. user partition `rbuergi` with
                //    content only in satellites). Synthesize a placeholder
                //    MeshNode so every downstream consumer (routing,
                //    MessageHubGrain.OnActivateAsync's source stream filter)
                //    sees a concrete Node — null would otherwise deadlock
                //    grain activation by failing the `Where(Node is not null)`
                //    gate. The placeholder carries the bare partition path
                //    and no NodeType so enrichment falls back to the default
                //    hub config — the user partition's hub then renders the
                //    standard portal layout (start screen, user activity).
                if (segments.Length >= 1 && IsRegisteredPartition(segments[0]))
                {
                    var partitionRoot = new MeshNode(segments[0])
                    {
                        Name = segments[0],
                        State = MeshNodeState.Active
                    };
                    return BuildResolution(segments[0], segments, matchedSegments: 1, matchedNode: partitionRoot);
                }

                _logger?.LogDebug("[RESOLVE] {Path} → NULL (no match across all steps)", path);
                return null;
            });
    }

    private (MeshNode? Node, int Depth)? ProbeStaticNodes(string[] segments)
    {
        if (_staticNodeProviders.Length == 0)
            return null;
        var staticNodes = _staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .ToArray();
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var testPath = string.Join("/", segments.Take(depth));
            var staticNode = staticNodes.FirstOrDefault(n =>
                string.Equals(n.Path, testPath, StringComparison.OrdinalIgnoreCase));
            if (staticNode != null)
                return (staticNode, depth);
        }
        return null;
    }

    private bool IsRegisteredPartition(string firstSegment) =>
        _partitionStorageProviders.Any(p => p.Matches(firstSegment));

    private static int ScoreMatch(MeshNode node, string[] segments)
    {
        var nodeSegments = node.Path.Split('/');
        if (nodeSegments.Length > segments.Length) return 0;
        for (int i = 0; i < nodeSegments.Length; i++)
            if (!string.Equals(nodeSegments[i], segments[i], StringComparison.OrdinalIgnoreCase))
                return 0;
        return nodeSegments.Length;
    }

    private static int MatchedSegments(string matchedPath) =>
        string.IsNullOrEmpty(matchedPath) ? 0 : matchedPath.Split('/').Length;

    private static AddressResolution BuildResolution(string matchedPath, string[] requestedSegments, int matchedSegments, MeshNode? matchedNode)
    {
        var remainder = matchedSegments < requestedSegments.Length
            ? string.Join("/", requestedSegments.Skip(matchedSegments))
            : null;
        return new AddressResolution(matchedPath, remainder, matchedNode);
    }

    /// <summary>
    /// True when a catalog change at <paramref name="changedPath"/> could
    /// plausibly affect the resolution of <paramref name="resolvingPath"/>:
    /// equal, ancestor, or descendant. Conservative — false negatives stall
    /// live consumers; false positives just re-resolve harmlessly.
    /// </summary>
    private static bool MightAffect(string resolvingPath, string changedPath)
    {
        var changed = changedPath.TrimStart('/');
        if (string.IsNullOrEmpty(changed)) return false;
        if (resolvingPath.Equals(changed, StringComparison.OrdinalIgnoreCase)) return true;
        if (resolvingPath.StartsWith(changed + "/", StringComparison.OrdinalIgnoreCase)) return true;
        if (changed.StartsWith(resolvingPath + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Resolution-equality for <see cref="DistinctUntilChanged"/>. Path-shape
    /// (Prefix + Remainder) is the cache key; the Node is metadata that can
    /// change without altering the route (e.g. a Name/Description edit on the
    /// matched node would otherwise spam re-emissions through the routing
    /// pipeline).
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

    private void OnCreated(MeshChangeEvent e)
    {
        var path = e.Path.TrimStart('/');
        _logger?.LogDebug("PathResolution cache: Created {Path}", path);
        EvictAffected(path);
        _catalogChanges.OnNext(e);
    }

    private void OnDeleted(MeshChangeEvent e)
    {
        var path = e.Path.TrimStart('/');
        _logger?.LogDebug("PathResolution cache: Deleted {Path}", path);
        EvictAffected(path);
        _catalogChanges.OnNext(e);
    }

    /// <summary>
    /// Removes cached streams whose resolution could be affected by a change
    /// at <paramref name="changedPath"/>. The next subscriber to such a path
    /// gets a fresh stream — and any existing subscribers re-emit through
    /// the <see cref="_catalogChanges"/> Subject (Replay(1).RefCount keeps
    /// them connected). Same MightAffect predicate as the live filter.
    /// </summary>
    private void EvictAffected(string changedPath)
    {
        foreach (var key in _streams.Keys)
        {
            if (MightAffect(key, changedPath))
                _streams.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _createSub?.Dispose();
        _deleteSub?.Dispose();
        _catalogChanges.OnCompleted();
        _catalogChanges.Dispose();
        _streams.Clear();
    }
}
