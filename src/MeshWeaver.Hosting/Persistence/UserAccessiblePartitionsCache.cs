using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Per-user (scoped) cache of accessible partition providers. Holds an
/// <see cref="ImmutableDictionary{TKey,TValue}"/> in a <see cref="ReplaySubject{T}"/>
/// (buffer 1) so consumers — chat autocomplete, partition listings — get the current
/// snapshot synchronously and any future invalidation reactively, without having to
/// re-run partition discovery + per-user filtering on every keystroke.
///
/// <para>Pre-warm: the constructor kicks off an
/// <see cref="Observable.FromAsync{TResult}(Func{CancellationToken,Task{TResult}},IScheduler)"/>
/// on <see cref="Scheduler.Default"/> that calls
/// <see cref="RoutingPersistenceServiceCore.DiscoverNewProvidersAsync"/>
/// + <see cref="ICrossSchemaQueryProvider.GetSearchableSchemasAsync"/>, builds the
/// filtered map, and pushes it into the subject. The constructor returns immediately
/// — the cache is "warming" in the background; subscribers wait for the first
/// emission, then everything subsequent is instant.</para>
///
/// <para>Fan-out is intentionally absent. <c>@/&lt;filter&gt;</c> only needs the
/// partition KEYS (single-segment paths). Diving into a partition's contents only
/// happens when the user types the second slash (<c>@/Partition/</c>), and that
/// request routes directly to one provider — no list scan, no global fan-out.</para>
///
/// <para>Invalidation: a public <see cref="Refresh"/> method re-runs the fetch and
/// emits a new snapshot. Hook it to events that change the partition set
/// (CreatePartition, schema migration, etc.).</para>
/// </summary>
internal sealed class UserAccessiblePartitionsCache : IDisposable
{
    private readonly RoutingPersistenceServiceCore _router;
    private readonly ICrossSchemaQueryProvider? _crossSchemaProvider;
    private readonly ILogger? _logger;

    // ReplaySubject(1) — no initial value. Subscribers wait for the first fetch to
    // complete, then see that value (and every subsequent invalidation). New
    // subscribers always get the latest snapshot.
    private readonly ReplaySubject<ImmutableDictionary<string, IMeshQueryProvider>> _subject = new(bufferSize: 1);

    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

    public UserAccessiblePartitionsCache(
        RoutingPersistenceServiceCore router,
        ICrossSchemaQueryProvider? crossSchemaProvider = null,
        ILogger<UserAccessiblePartitionsCache>? logger = null)
    {
        _router = router;
        _crossSchemaProvider = crossSchemaProvider;
        _logger = logger;

        // Kick off the first fetch eagerly. Pure reactive: Observable.FromAsync runs
        // on the ThreadPool (no Orleans/Blazor scheduler captured at construction).
        _disposables.Add(
            BuildSnapshot()
                .Subscribe(
                    map =>
                    {
                        _logger?.LogDebug("[PartitionsCache] warmed with {Count} partitions", map.Count);
                        _subject.OnNext(map);
                    },
                    ex =>
                    {
                        _logger?.LogWarning(ex, "[PartitionsCache] initial warm failed — emitting empty snapshot");
                        _subject.OnNext(ImmutableDictionary<string, IMeshQueryProvider>.Empty);
                    }));
    }

    /// <summary>
    /// The latest accessible-partitions snapshot. Subscribers receive the current
    /// value (after warm-up) and every subsequent <see cref="Refresh"/>.
    /// </summary>
    public IObservable<ImmutableDictionary<string, IMeshQueryProvider>> Partitions => _subject;

    /// <summary>
    /// Re-runs partition discovery + per-user filtering and emits a new snapshot.
    /// Call after creating a new partition or when access-control changes for the user.
    /// </summary>
    public void Refresh()
    {
        _disposables.Add(
            BuildSnapshot()
                .Subscribe(
                    map =>
                    {
                        _logger?.LogDebug("[PartitionsCache] refreshed: {Count} partitions", map.Count);
                        _subject.OnNext(map);
                    },
                    ex => _logger?.LogWarning(ex, "[PartitionsCache] refresh failed")));
    }

    private IObservable<ImmutableDictionary<string, IMeshQueryProvider>> BuildSnapshot()
        => Observable.FromAsync(BuildSnapshotAsync, Scheduler.Default);

    private async Task<ImmutableDictionary<string, IMeshQueryProvider>> BuildSnapshotAsync(CancellationToken ct)
    {
        // Provision any newly-discovered partitions (idempotent).
        await foreach (var _ in _router.DiscoverNewProvidersAsync(ct))
        { /* side effect: _router.QueryProviders updated */ }

        // Per-user accessible-schemas filter (Postgres uses public.searchable_schemas
        // which already enforces RLS for the calling user). null → no filtering
        // (file-system fixtures, in-memory mesh).
        HashSet<string>? searchable = null;
        if (_crossSchemaProvider != null)
        {
            var schemas = await _crossSchemaProvider.GetSearchableSchemasAsync(ct);
            searchable = schemas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var builder = ImmutableDictionary.CreateBuilder<string, IMeshQueryProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, provider) in _router.QueryProviders)
        {
            if (searchable != null && !searchable.Contains(key)) continue;
            builder[key] = provider;
        }
        return builder.ToImmutable();
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _subject.Dispose();
    }
}
