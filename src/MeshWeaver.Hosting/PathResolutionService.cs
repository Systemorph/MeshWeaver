using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MeshWeaver.Hosting;

/// <summary>
/// Resolves URL paths to hub addresses with an internally managed cache.
/// Subscribes to <see cref="IMeshChangeFeed"/> to invalidate cache entries
/// on create/delete — this is an implementation detail, not exposed in the API.
///
/// <para>
/// <b>Live reactive resolution</b>. <see cref="ResolvePath"/> returns an
/// observable that emits the current resolution AND re-emits whenever the
/// underlying catalog state changes such that the resolution might differ
/// (any <see cref="MeshChangeKind.Created"/> or <see cref="MeshChangeKind.Deleted"/>).
/// Subscribers stay live — no polling, no retry timers. Once a previously-
/// missing node lands in the catalog, the next emission is non-null and the
/// subscriber's logic fires immediately. Replaces the old "resolve once,
/// retry on a Timer" pattern in NavigationService that had to swallow stale
/// retries via a separate <c>_currentPath</c> snapshot check.
/// </para>
///
/// <para>
/// In Orleans: register as singleton on each silo. Each silo maintains its
/// own cache. Cross-silo invalidation happens via Orleans streams →
/// local IMeshChangeFeed.
/// </para>
/// </summary>
internal class PathResolutionService : IPathResolver, IDisposable
{
    private readonly MeshCatalog _catalog;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pathTokens = new();
    private readonly IDisposable? _createSub;
    private readonly IDisposable? _deleteSub;
    private readonly ILogger<PathResolutionService>? _logger;
    // Fan-out for catalog change notifications. Every Created/Deleted event the
    // change feed delivers gets pushed onto this subject; ResolvePath's
    // observable composes against it so subscribers re-resolve automatically.
    private readonly Subject<MeshChangeEvent> _catalogChanges = new();

    public PathResolutionService(
        MeshCatalog catalog,
        IMeshChangeFeed? changeFeed = null,
        ILogger<PathResolutionService>? logger = null)
    {
        _catalog = catalog;
        _logger = logger;

        // Subscribe internally — cache invalidation is our concern, nobody else's.
        // The handlers also push the change onto _catalogChanges so live
        // ResolvePath observers re-evaluate.
        _createSub = changeFeed?.Subscribe(OnCreated, MeshChangeKind.Created);
        _deleteSub = changeFeed?.Subscribe(OnDeleted, MeshChangeKind.Deleted);
    }

    public IObservable<AddressResolution?> ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        path = path.TrimStart('/');
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        // Live stream: initial emission + re-emit whenever a relevant catalog
        // change arrives. Filter the change stream to events that could affect
        // this path (the path itself or any prefix/descendant of it). Initial
        // tick via StartWith(Unit) so we always get one emission even with no
        // upstream changes. DistinctUntilChanged collapses redundant re-emits
        // when several unrelated events arrive in succession.
        return _catalogChanges
            .Where(e => MightAffect(path, e.Path))
            .Select(_ => System.Reactive.Unit.Default)
            .StartWith(System.Reactive.Unit.Default)
            .SelectMany(_ => ResolveOnce(path))
            .DistinctUntilChanged(AddressResolutionEquality.Instance);
    }

    private IObservable<AddressResolution?> ResolveOnce(string path)
    {
        var cacheKey = $"resolve:{path}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached is AddressResolution resolution)
            return Observable.Return<AddressResolution?>(resolution);

        // Delegate to MeshCatalog — IObservable end-to-end; FromAsync only at the
        // DB-leaf hits inside MeshCatalog.ResolvePathCore.
        return _catalog.ResolvePathCore(path)
            .Do(result =>
            {
                if (result != null)
                    CacheResolution(path, result);
            });
    }

    /// <summary>
    /// True when a catalog change at <paramref name="changedPath"/> could plausibly
    /// affect the resolution of <paramref name="resolvingPath"/> — they're equal,
    /// changedPath is a prefix of resolvingPath (a parent appearing/disappearing
    /// changes the closest-ancestor match), or resolvingPath is a prefix of
    /// changedPath (a deeper node appearing under our resolution target may
    /// change the matched leaf). Conservative — false negatives would silently
    /// stall live consumers; false positives just re-resolve harmlessly.
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
        InvalidateSubtree(path);
        CacheResolution(path, new AddressResolution(path, null));
        _catalogChanges.OnNext(e);
    }

    private void OnDeleted(MeshChangeEvent e)
    {
        var path = e.Path.TrimStart('/');
        _logger?.LogDebug("PathResolution cache: Deleted {Path}", path);
        InvalidateSubtree(path);
        _catalogChanges.OnNext(e);
    }

    private void InvalidateSubtree(string path)
    {
        foreach (var key in _pathTokens.Keys)
        {
            if (key.Equals(path, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            {
                if (_pathTokens.TryRemove(key, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }
            }
        }
    }

    private void CacheResolution(string path, AddressResolution resolution)
    {
        var cts = _pathTokens.GetOrAdd(path, _ => new CancellationTokenSource());
        var options = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .AddExpirationToken(new CancellationChangeToken(cts.Token));
        _cache.Set($"resolve:{path}", resolution, options);
    }

    public void Dispose()
    {
        _createSub?.Dispose();
        _deleteSub?.Dispose();
        _catalogChanges.OnCompleted();
        _catalogChanges.Dispose();
        _cache.Dispose();
        foreach (var cts in _pathTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
