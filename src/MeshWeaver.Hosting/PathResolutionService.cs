using System.Collections.Concurrent;
using System.Reactive.Linq;
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
/// In Orleans: register as singleton on each silo. Each silo maintains its own cache.
/// Cross-silo invalidation happens via Orleans streams → local IMeshChangeFeed.
/// </summary>
internal class PathResolutionService : IPathResolver, IDisposable
{
    private readonly MeshCatalog _catalog;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pathTokens = new();
    private readonly IDisposable? _createSub;
    private readonly IDisposable? _deleteSub;
    private readonly ILogger<PathResolutionService>? _logger;

    public PathResolutionService(
        MeshCatalog catalog,
        IMeshChangeFeed? changeFeed = null,
        ILogger<PathResolutionService>? logger = null)
    {
        _catalog = catalog;
        _logger = logger;

        // Subscribe internally — cache invalidation is our concern, nobody else's
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

        var cacheKey = $"resolve:{path}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached is AddressResolution resolution)
            return Observable.Return<AddressResolution?>(resolution);

        // Delegate to MeshCatalog (storage I/O, no hub round-trip — bridging via
        // Observable.FromAsync is safe at this storage boundary; see
        // Doc/Architecture/AsynchronousCalls.md).
        return Observable.FromAsync(() => _catalog.ResolvePathCoreAsync(path))
            .Do(result =>
            {
                if (result != null)
                    CacheResolution(path, result);
            });
    }

    private void OnCreated(MeshChangeEvent e)
    {
        var path = e.Path.TrimStart('/');
        _logger?.LogDebug("PathResolution cache: Created {Path}", path);
        InvalidateSubtree(path);
        CacheResolution(path, new AddressResolution(path, null));
    }

    private void OnDeleted(MeshChangeEvent e)
    {
        var path = e.Path.TrimStart('/');
        _logger?.LogDebug("PathResolution cache: Deleted {Path}", path);
        InvalidateSubtree(path);
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
        _cache.Dispose();
        foreach (var cts in _pathTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
