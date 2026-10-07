using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Decorates an <see cref="IStorageAdapter"/> so every <see cref="IStorageAdapter.Write"/>
/// chains a snapshot through <see cref="IVersionQuery.WriteVersion"/> after the inner
/// save succeeds. Replaces the historical
/// <c>FileSystemPersistenceService.SaveNodeAsync</c> chokepoint that was deleted in the
/// persistence cull (2026-05-12) — without this, every save path
/// (CreateNode / UpdateNode handlers, MeshNodeTypeSource flush, sampler) skipped the
/// version-history write and <c>IVersionQuery.GetVersions</c> returned an empty list.
///
/// <para>Best-effort: version-write failures are swallowed so they cannot mask a
/// successful primary save.</para>
///
/// <para><b>Types that keep no history.</b> A node whose NodeType declares
/// <see cref="NodeTypeDefinition.KeepsHistory"/> = <c>false</c> gets NO snapshot from this
/// decorator; instead its history is PURGED (<see cref="IVersionQuery.PurgeVersions"/>) after
/// every write — a store that snapshots on its own, the Postgres
/// <c>mesh_node_copy_to_history</c> trigger, has already recorded one by then — and after the
/// node is deleted. The type's definition is read HERE, off the storage this decorator wraps
/// (an in-mesh NodeType is a row like any other) or the host's static nodes (a C#-registered
/// type), so the decision needs no hub and no routing. A definition that cannot be read keeps
/// history: the failure mode is a retained snapshot, never a destroyed one. See
/// <c>Doc/Architecture/MeshNodeVersioning</c> → "Types That Keep No History".</para>
/// </summary>
/// <param name="inner">The adapter being decorated.</param>
/// <param name="versionQuery">The version store, or null when none is registered.</param>
/// <param name="staticNodeLookup">Resolves a host-registered (static) node by path — the
/// C#-registered NodeType definitions. Null means "no static nodes" (a bare test stack).</param>
/// <param name="readOptions">The serializer options used to read a node on the DELETE path, where
/// the adapter API hands none in. Null falls back to default options (enough to read a node's
/// <c>nodeType</c> and a definition's <c>keepsHistory</c>).</param>
/// <param name="logger">Optional logger for purges a store could not perform.</param>
internal class VersionWritingStorageAdapter(
    IStorageAdapter inner,
    IVersionQuery? versionQuery,
    Func<string, MeshNode?>? staticNodeLookup = null,
    Func<JsonSerializerOptions?>? readOptions = null,
    ILogger? logger = null) : IStorageAdapter
{
    /// <summary>How long reading a NodeType definition may take before the decision falls back to
    /// keeping history.</summary>
    private static readonly TimeSpan DefinitionReadTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions FallbackReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The resolved <see cref="NodeTypeDefinition.KeepsHistory"/> verdict per node type (keyed by
    /// type path), so a write pays the definition lookup once per type, not once per write.
    /// Instance field on the mesh-scoped singleton — never static.
    ///
    /// <para>🚨 Invalidated, never expired: an entry goes when the DEFINITION changes — synchronously
    /// when this decorator writes or deletes it, and through <see cref="IStorageAdapter.Changes"/>
    /// (the cross-replica change feed on Postgres) when another replica does. Only DEFINITE answers
    /// are cached; a timed-out or faulted read keeps history for that one write and is asked again
    /// next time. A write in the window before another replica's opt-out reaches this feed still
    /// gets a snapshot — and the next write or the delete of that node purges it, so nothing
    /// outlives the window.</para>
    ///
    /// <para>Why: MeshWeaver#5886 looked the definition up on EVERY write (a static-node
    /// enumeration or a routed storage read), and the platform bake's
    /// <c>Hosting/FleetConsole</c> gate — three sequential <c>CreateOrUpdateNode</c>s inside a 10 s
    /// budget — went red on the first two sets carrying it and on none before.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedVerdict> verdicts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One type's cache entry: the invalidation <paramref name="Generation"/> and the verdict
    /// resolved AT that generation (null = none). ONE entry replaced atomically, so an install and an
    /// invalidation of the same type can never interleave: <see cref="Remember"/> installs only by
    /// compare-and-swap against the exact entry its read started from, and <see cref="Invalidate"/>
    /// always moves the generation on, which makes that swap fail.</summary>
    private sealed record CachedVerdict(long Generation, bool? Keeps);

    private int invalidationArmed;

    /// <summary>When the change feed last ended (fault or completion), in UTC ticks; 0 = never.
    /// While it is recent the cache is bypassed rather than re-armed on every write — see
    /// <see cref="ArmInvalidation"/>.</summary>
    private long feedEndedAtTicks;

    /// <summary>How long after the change feed ended before a re-subscription is attempted.</summary>
    private static readonly TimeSpan FeedRearmInterval = TimeSpan.FromMinutes(1);

    // 🚨 Decorator MUST forward Changes — without this it falls back to the
    // interface default Observable.Empty, and every synced query subscribed
    // to persistence.Changes on this decorator stops receiving notifications.
    // The IDataChangeNotifier removal refactor (929bfe985) moved change-feed
    // delivery to IStorageAdapter.Changes — at which point this decorator's
    // missing override silently became the new bottleneck. Symptom in CI
    // (26408564176): ~25 Security / Auth / NodeOps / Layout failures where
    // the synced AccessAssignment query only emitted its Initial = 0 and
    // never re-emitted after CreateNode runtime writes, so permission grants
    // never reached the AccessControlPipeline before its 45 s deadline.
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    /// <inheritdoc />
    public IObservable<ChangeFeedGap> ChangeFeedGaps => inner.ChangeFeedGaps;

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 <b>Decorator MUST forward ReadMany</b> — the same rule, and the same silence, as
    /// <see cref="Changes"/> above. Without this the interface default applies
    /// (<c>Observable.Merge(paths.Select(Read))</c>), so a batch handed to this decorator is
    /// fanned back out into one point read per path THROUGH <c>this</c>, and the batched
    /// implementation below (<c>PersistenceService.ReadMany</c> → a backend's
    /// <c>WHERE path = ANY($1)</c>) is never reached. This decorator is the production chain's
    /// third layer — <c>SubtreeDeletionGuard → MonotonicWriteGuard → VersionWriting → inner</c>,
    /// built by <c>PersistenceExtensions.DecorateStorageAdapterWithVersionWriting</c> — so its
    /// missing override alone degraded EVERY batched read in every deployed host, whatever the
    /// layers around it did (#4200).
    ///
    /// <para>Pure delegation is the whole implementation: a READ records no version history, so
    /// there is nothing for this decorator to add on the way past — exactly like
    /// <see cref="Read"/>.</para>
    /// </remarks>
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => inner.ReadMany(paths, options);

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
    {
        Invalidate(node.Path);
        var write = inner.Write(node, options);
        if (versionQuery is null)
            return write;

        return write.SelectMany(saved => saved is null
            ? Observable.Return<MeshNode?>(null)
            : KeepsHistory(saved.NodeType, options)
                .SelectMany(keeps => keeps
                    ? WriteVersionAndReturn(saved, options)
                    : Purge(saved.Path).Select(_ => (MeshNode?)saved)));
    }

    private IObservable<MeshNode?> WriteVersionAndReturn(MeshNode saved, JsonSerializerOptions options) =>
        versionQuery!.WriteVersion(saved, options)
            .Catch<MeshNode, Exception>(_ => Observable.Return(saved))
            .DefaultIfEmpty(saved)
            .LastAsync()
            .Select(_ => (MeshNode?)saved);

    /// <summary>
    /// Explicit forward — the interface default would read-compare-write through <c>this</c> and
    /// strip the backend's atomic compare-and-set. An APPLIED write gets its version-history
    /// snapshot exactly like <see cref="Write"/>; a refused one deliberately gets none (nothing
    /// changed durably, so there is no revision to record).
    /// </summary>
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
    {
        Invalidate(node.Path);
        var write = inner.WriteIfVersion(node, expectedVersion, options);
        if (versionQuery is null)
            return write;

        return write.SelectMany(applied => applied is true
            ? KeepsHistory(node.NodeType, options)
                .SelectMany(keeps => keeps
                    ? versionQuery.WriteVersion(node, options)
                        .Catch<MeshNode, Exception>(_ => Observable.Empty<MeshNode>())
                        .DefaultIfEmpty(node)
                        .LastAsync()
                        .Select(_ => applied)
                    : Purge(node.Path).Select(_ => applied))
            : Observable.Return(applied));
    }

    /// <inheritdoc />
    /// <remarks>Reads the node FIRST (its type decides), deletes it, then purges its history when
    /// its type keeps none.</remarks>
    public IObservable<string> Delete(string path)
    {
        Invalidate(path);
        if (versionQuery is null)
            return inner.Delete(path);
        return PathsWithoutHistory([path])
            .SelectMany(purge => inner.Delete(path)
                .SelectMany(deleted => PurgeAll(purge).Select(_ => deleted)));
    }

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path)
    {
        Invalidate(path);
        if (versionQuery is null)
            return inner.DeleteIfExists(path);
        return PathsWithoutHistory([path])
            .SelectMany(purge => inner.DeleteIfExists(path)
                .SelectMany(existed => PurgeAll(purge).Select(_ => existed)));
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
    {
        foreach (var path in paths)
            Invalidate(path);
        if (versionQuery is null || paths.Count == 0)
            return inner.DeleteMany(paths);
        return PathsWithoutHistory(paths)
            .SelectMany(purge => inner.DeleteMany(paths)
                .SelectMany(deleted => PurgeAll(purge.Where(p => deleted.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray())
                    .Select(_ => deleted)));
    }

    /// <summary>
    /// Whether nodes of <paramref name="nodeType"/> keep version history — the type's
    /// <see cref="NodeTypeDefinition.KeepsHistory"/>. Emits exactly once. An absent, untyped or
    /// unreadable definition answers <c>true</c>: the safe failure is a kept snapshot.
    /// </summary>
    private IObservable<bool> KeepsHistory(string? nodeType, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(nodeType)
            || string.Equals(nodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            return Observable.Return(true);

        if (!ArmInvalidation())
            // No live feed can invalidate a cached verdict, so none is used or kept: every write
            // resolves its type afresh until the feed is back.
            return ResolveKeepsHistory(nodeType, readOptions?.Invoke() ?? options, cache: false);
        if (verdicts.TryGetValue(nodeType, out var entry) && entry.Keeps is { } cached)
            return Observable.Return(cached);
        return ResolveKeepsHistory(nodeType, readOptions?.Invoke() ?? options, cache: true);
    }

    /// <summary>
    /// The uncached lookup behind <see cref="KeepsHistory"/>; caches a definite answer. Resolved with
    /// the adapter's OWN read options when it has them, so the cached verdict does not depend on
    /// whichever writer happened to ask first.
    /// </summary>
    private IObservable<bool> ResolveKeepsHistory(string nodeType, JsonSerializerOptions options, bool cache)
    {
        // Registering the type here (and only here) keeps the map bounded by the number of TYPES
        // ever asked about, never by the number of paths written.
        var started = cache ? verdicts.GetOrAdd(nodeType, new CachedVerdict(0, null)) : null;

        // A slash-less type is never an in-mesh row (a definition always lives INSIDE a partition —
        // the rule PartitionOwningTypes applies), so only the host's static nodes can declare it.
        // Those come from MeshBuilder.AddMeshNodes and the IStaticNodeProviders registered when the
        // mesh is built (StaticNodeProviderExtensions.ResolveStaticNodes owns that resolution); a
        // plugin installed at runtime brings in-mesh NodeTypes — storage rows, covered by both
        // invalidation paths — never static ones. So this verdict needs no invalidation.
        if (!nodeType.Contains('/'))
            return Observable.Return(Remember(nodeType, started,
                staticNodeLookup?.Invoke(nodeType) is not { } builtIn || Declares(builtIn, options)));

        // A path-shaped type is read off the storage this decorator wraps FIRST — the common case
        // (an in-mesh type) is one primary-key read, and the full static-node enumeration is paid
        // only for a type the store does not hold. Once per type: see `verdicts`.
        return inner.Read(nodeType, options)
            .Take(1)
            .DefaultIfEmpty(null)
            .Select(definition => Remember(nodeType, started,
                (definition ?? staticNodeLookup?.Invoke(nodeType)) is not { } found || Declares(found, options)))
            .Timeout(DefinitionReadTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "Reading NodeType {NodeType} to decide whether it keeps version history did not answer within {Timeout} — keeping history for this write",
                    nodeType, DefinitionReadTimeout);
                return Observable.Return(true);
            }))
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Could not read NodeType {NodeType} to decide whether it keeps version history — keeping it",
                    nodeType);
                return Observable.Return(true);
            });
    }

    /// <summary>Installs a verdict by compare-and-swap against the entry the read started from, so a
    /// type invalidated at ANY point after that — before the check or between check and store — keeps
    /// no verdict from it.</summary>
    private bool Remember(string nodeType, CachedVerdict? started, bool keeps)
    {
        if (started is not null)
            verdicts.TryUpdate(nodeType, started with { Keeps = keeps }, started);
        return keeps;
    }

    /// <summary>Drops the cached verdict for a definition that was written or deleted, moving its
    /// generation on so a verdict still being resolved from the old definition is not installed.</summary>
    private void Invalidate(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        // Only a type someone resolved has an entry; a written path that is not one is ignored (no
        // entry is created, so the map stays bounded by the number of types).
        while (verdicts.TryGetValue(path, out var current)
               && !verdicts.TryUpdate(path, new CachedVerdict(current.Generation + 1, null), current))
        {
        }
    }

    /// <summary>
    /// Subscribes to the wrapped store's change feed (once while it lives), dropping the cached
    /// verdict of any definition another writer — another replica, or a write that bypassed this
    /// decorator — changed. Returns whether a live feed backs the cache.
    ///
    /// <para>If the feed ENDS (faults, or completes), every verdict is forgotten and the cache is
    /// bypassed; a re-subscription is attempted at most once per <see cref="FeedRearmInterval"/>, so
    /// a persistently broken feed degrades to "uncached, one warning a minute", never to a
    /// clear/re-arm/warn loop per write.</para>
    /// </summary>
    private bool ArmInvalidation()
    {
        if (Volatile.Read(ref invalidationArmed) == 1)
            return true;
        var endedAt = Interlocked.Read(ref feedEndedAtTicks);
        if (endedAt != 0 && DateTimeOffset.UtcNow.UtcTicks - endedAt < FeedRearmInterval.Ticks)
            return false;
        if (Interlocked.CompareExchange(ref invalidationArmed, 1, 0) != 0)
            return true;
        inner.Changes.Subscribe(
            change => Invalidate(change.Path),
            ex => FeedEnded(ex, "faulted"),
            () => FeedEnded(null, "completed"));
        return Volatile.Read(ref invalidationArmed) == 1;
    }

    private void FeedEnded(Exception? ex, string how)
    {
        logger?.LogWarning(ex,
            "The storage change feed {How} — dropping every cached keeps-history verdict; types are resolved per write until it is re-subscribed (at most once a minute)",
            how);
        Interlocked.Exchange(ref feedEndedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        verdicts.Clear();
        Interlocked.Exchange(ref invalidationArmed, 0);
    }

    /// <summary>
    /// Whether the definition node does NOT opt out. Total by construction: a node type is an
    /// unvalidated string, so the node at that path may hold content of any shape, and a throw
    /// from inside a <c>Select</c> selector would escape the adjacent <c>Catch</c> and fault a
    /// write that already committed. Anything unreadable keeps history.
    /// </summary>
    private bool Declares(MeshNode definition, JsonSerializerOptions options)
    {
        try
        {
            return definition.ContentAs<NodeTypeDefinition>(options) is not { KeepsHistory: false };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "The node at {Path} could not be read as a NodeType definition — keeping version history",
                definition.Path);
            return true;
        }
    }

    /// <summary>
    /// The subset of <paramref name="paths"/> whose node exists and whose type keeps no history —
    /// read BEFORE the delete, since after it there is no node to ask. Emits exactly once.
    /// </summary>
    private IObservable<IReadOnlyList<string>> PathsWithoutHistory(IReadOnlyCollection<string> paths)
    {
        var options = readOptions?.Invoke() ?? FallbackReadOptions;
        return inner.ReadMany(paths, options)
            .ToList()
            .SelectMany(nodes => nodes
                .Select(n => n.NodeType)
                // A node with no type keeps history (KeepsHistory answers true for it) — and a null
                // would throw inside StringComparer.Ordinal.GetHashCode, from a selector, past the
                // Catch below, failing the delete itself.
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(type => KeepsHistory(type, options).Select(keeps => (Type: type, Keeps: keeps)))
                .Concat()
                .ToList()
                .Select(verdicts =>
                {
                    var noHistory = verdicts.Where(v => !v.Keeps).Select(v => v.Type).ToHashSet(StringComparer.Ordinal);
                    return (IReadOnlyList<string>)nodes
                        .Where(n => n.NodeType is { } t && noHistory.Contains(t))
                        .Select(n => n.Path)
                        .ToArray();
                }))
            .Catch<IReadOnlyList<string>, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Could not read the nodes being deleted to decide whether their history is purged — keeping it");
                return Observable.Return<IReadOnlyList<string>>([]);
            });
    }

    /// <summary>Purges every path in turn (one at a time — <c>Concat</c>, never a burst), then emits
    /// once.</summary>
    private IObservable<Unit> PurgeAll(IReadOnlyList<string> paths)
        => paths.Count == 0
            ? Observable.Return(Unit.Default)
            : paths.Select(Purge).Concat().LastAsync().Select(_ => Unit.Default);

    /// <summary>
    /// Removes a no-history node's recorded versions. Best-effort like the snapshot write: a store
    /// that cannot purge (or faults) never fails the primary write or delete, but it is LOGGED —
    /// a retained snapshot of a type that declared it keeps none is exactly the fact an operator
    /// has to be able to see.
    /// </summary>
    /// <remarks>Only reached when a version store exists: <see cref="Write"/> and
    /// <see cref="WriteIfVersion"/> return the bare inner write when <c>versionQuery</c> is null,
    /// and the three deletes do the same.</remarks>
    private IObservable<bool> Purge(string path)
        => versionQuery!.PurgeVersions(path)
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex, "Purging the version history of {Path} faulted — its history is retained", path);
                return Observable.Return(false);
            })
            .DefaultIfEmpty(false)
            .LastAsync()
            .Do(purged =>
            {
                if (!purged)
                    logger?.LogWarning(
                        "The version store {Store} cannot purge the history of {Path}, whose NodeType keeps no history — its snapshots are retained",
                        versionQuery.GetType().Name, path);
            });

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <summary>
    /// Explicit forward — the interface default would walk <c>this.ListChildPaths</c>
    /// and strip the backend's native prefix enumeration (same reason as
    /// <see cref="ResolvePath"/> below).
    /// </summary>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    /// <remarks>Pure delegation — only the composite below knows its providers, and the
    /// interface default (<c>null</c>) would silently drop the delete pre-flight (#1433).</remarks>
    public IObservable<string?> FindDeleteBlockingProvider(string path)
        => inner.FindDeleteBlockingProvider(path);

    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<bool> ExistsInWritableStorage(string path)
        => inner.ExistsInWritableStorage(path);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <summary>
    /// Explicit forward — without this, the interface default routes through
    /// <c>this.FindBestPrefixMatch</c>, stripping the Postgres satellite-UNION
    /// that <c>MeshWeaver.Hosting.PostgreSql.PostgreSqlPathRoutingAdapter.ResolvePath</c>
    /// produces. FileSystem doesn't need the override (its
    /// <c>FindBestPrefixMatch</c> already walks segments), but preserving the
    /// stronger Postgres contract requires the forward.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
