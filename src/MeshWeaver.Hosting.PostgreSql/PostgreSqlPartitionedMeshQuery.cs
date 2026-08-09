using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IMeshQueryProvider"/> for partitioned setups —
/// the one Postgres-aware brain in the <see cref="MeshQuery"/> fan-in chain.
///
/// <para><b>The architectural contract.</b> <see cref="MeshQuery"/> delegates
/// every query to every registered provider and merges the returned
/// observables. Each provider is responsible for the WHOLE shape of its own
/// data domain — the aggregator never tells a provider "you handle only X."
/// For Postgres, that means this provider alone reacts to a missing
/// namespace (unscoped query) or a wildcard first segment by fanning out
/// across every searchable partition. Fan-out is an implementation detail
/// of the Postgres provider, NOT a separate provider type, NOT a concern
/// of <c>MeshSearch</c> or any GUI control.</para>
///
/// <para><b>Two query shapes, one provider:</b></para>
/// <list type="number">
///   <item><b>Scoped</b> (single concrete first segment, no wildcard) →
///     short-circuit to an empty Initial emission. The pedestrian
///     <see cref="StorageAdapterMeshQueryProvider"/> backed by the
///     <see cref="IStorageAdapter"/> path-routing facade still runs in the
///     fan-in chain; for a scoped Postgres path it routes directly to the
///     per-schema adapter and contributes the actual rows. We deliberately
///     don't duplicate that work here — the result-merge in
///     <see cref="MeshQuery"/> dedupes by Path so returning the row twice
///     would be wasted load, not wrong.</item>
///   <item><b>Missing namespace / wildcard first segment</b> → fan out
///     across every searchable partition via
///     <see cref="ICrossSchemaQueryProvider.QueryAcrossSchemasAsync(ParsedQuery,JsonSerializerOptions,IReadOnlyList{string},string,string?,string?,CancellationToken)"/>.
///     Satellite-aware: a <c>nodeType:</c> filter routes the UNION to the
///     matching satellite table (Thread → <c>threads</c>, Activity →
///     <c>activities</c>, …); <c>source:activity</c> / <c>source:accessed</c>
///     turn into a per-schema INNER JOIN that projects the satellite's
///     <c>last_modified</c> into the result row so cross-partition
///     sort:LastModified-desc ranks by activity recency. Schema selection
///     filters to partitions that actually contain both the projection and
///     join tables — older partitions / static-mesh schemas (Doc, etc.)
///     only ship <c>mesh_nodes</c>.</item>
/// </list>
///
/// <para>Both shapes are LIVE — one Initial, then Added / Updated / Removed as writes land.
/// The cross-partition fan-out used to be a one-shot snapshot ("live deltas across partitions
/// are out of scope"), which silently made every PATH-LESS query permanently stale in a running
/// process: nothing else in the chain holds a change subscription for that query shape. The
/// permission evaluator's global <c>nodeType:GroupMembership</c> query is one of those, so group
/// membership could not be revoked without a restart (issue #697). See
/// <see cref="FanOutQuery{T}"/> for the relevance gate that keeps the re-query bounded.</para>
///
/// <para><b>Multi-query requests route PER QUERY.</b> A
/// <see cref="MeshQueryRequest.Queries"/> bundle can mix queries owned by
/// different partitions (a NodeType compile's source queries with a
/// <c>shared=@OtherPartition/...</c> entry are the canonical case). Each
/// query is parsed and routed to ITS owning partition's per-schema delegate;
/// queries with no concrete first-segment partition go to the cross-schema
/// fan-out; the per-group streams are merged into the provider's single
/// Initial-then-changes contract (see <c>MergePartitionStreams</c>).</para>
/// </summary>
public sealed class PostgreSqlPartitionedMeshQuery : IMeshQueryProvider
{
    private readonly ICrossSchemaQueryProvider _crossSchema;
    private readonly AccessService? _accessService;
    private readonly ILogger<PostgreSqlPartitionedMeshQuery>? _logger;
    private readonly PostgreSqlPartitionStorageProvider? _partitionProvider;
    private readonly IoPoolRegistry? _ioPoolRegistry;
    // Cross-schema fan-out + autocomplete run on the bounded I/O pool (Invoke), never a bare
    // Observable.FromAsync. The per-schema leaf reads are pooled inside their own adapters.
    private readonly IIoPool _ioPool;
    // Routing rules (nodeType:User → Auth, nodeType:Invitation → Admin) live here. A path-less
    // query that matches a rule must pin to the rule's schema instead of fanning out cross-schema
    // (which EXCLUDES auth/admin) — see the hint fallback in EnumerateFanOutAsync.
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly QueryParser _parser = new();
    // In-memory query matcher — used ONLY by the fan-out's live relevance gate to decide whether a
    // change notification's payload could enter this query's result set (see FanOutQuery). Never
    // used to produce results: the authoritative rows always come from the cross-schema re-query.
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    // Per-schema scoped-query delegates, keyed by the CACHED adapter instance (so each shares
    // that adapter's live in-process Changes feed). A SCOPED query — a concrete first path
    // segment, including _-prefix globals like _Access→system_access — is served by a per-schema
    // PostgreSqlMeshQuery (full live-delta + satellite serving). That is how THIS provider OWNS
    // scoped serving, letting the pedestrian StorageAdapterMeshQueryProvider be retired for
    // Postgres. See Doc/Architecture/PartitionStorageRouting.md.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PostgreSqlStorageAdapter, PostgreSqlMeshQuery> _scopedDelegates = new();

    /// <summary>
    /// Initializes the partition-aware fan-out query provider.
    /// </summary>
    /// <param name="crossSchema">Cross-schema provider used to UNION unscoped / wildcard queries across partitions.</param>
    /// <param name="accessService">Optional access service used to scope results to the calling user.</param>
    /// <param name="logger">Optional logger for query diagnostics.</param>
    /// <param name="partitionProvider">Optional partition storage provider used to resolve a scoped query to its owning partition's adapter.</param>
    /// <param name="ioPoolRegistry">Optional I/O pool registry; query leaves run on the shared storage-read pool.</param>
    /// <param name="meshConfiguration">Optional mesh configuration (node-type permissions, etc.).</param>
    public PostgreSqlPartitionedMeshQuery(
        ICrossSchemaQueryProvider crossSchema,
        AccessService? accessService = null,
        ILogger<PostgreSqlPartitionedMeshQuery>? logger = null,
        PostgreSqlPartitionStorageProvider? partitionProvider = null,
        IoPoolRegistry? ioPoolRegistry = null,
        MeshConfiguration? meshConfiguration = null)
    {
        _crossSchema = crossSchema;
        _accessService = accessService;
        _logger = logger;
        _partitionProvider = partitionProvider;
        _ioPoolRegistry = ioPoolRegistry;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        _meshConfiguration = meshConfiguration;
    }

    /// <summary>
    /// The per-schema <see cref="PostgreSqlMeshQuery"/> that serves a query SCOPED to the
    /// partition holding <paramref name="path"/>'s first segment — resolved (incl. <c>_</c>-prefix
    /// globals like <c>_Access</c>→<c>system_access</c>) to its CACHED adapter, one delegate per
    /// adapter so it observes that adapter's live <c>Changes</c> feed (post-write re-emits work).
    /// Null for unscoped / wildcard paths → those fan out across schemas.
    /// </summary>
    private PostgreSqlMeshQuery? GetDelegateForPath(string? path)
    {
        if (_partitionProvider is null || string.IsNullOrEmpty(path)) return null;
        var first = FirstSegment(path);
        if (string.IsNullOrEmpty(first) || first == "*") return null;
        var adapter = _partitionProvider.GetSchemaAdapter(path);
        if (adapter is null) return null;
        return _scopedDelegates.GetOrAdd(adapter, a =>
            new PostgreSqlMeshQuery(a, _accessService, meshConfiguration: null,
                excludedNamespaces: null, embeddingProvider: _partitionProvider.EmbeddingProvider,
                ioPoolRegistry: _ioPoolRegistry,
                // 🚨 The MERGED feed, not `a.Changes`. This delegate is the adapter for ONE schema,
                // but writes that change its results land in others — an AccessAssignment at
                // {Partition}/_Access/x publishes on the `system_access` adapter. Watching only
                // `a.Changes` meant that notification reached nobody in this fold, the
                // $security-access snapshot stayed frozen, and permissions evaluated stale.
                changeFeed: _partitionProvider.MergedChanges));
    }

    /// <summary>
    /// The scoped delegate for a parsed query, or null when the query must fan out across
    /// partitions. Routes to the per-schema <see cref="PostgreSqlMeshQuery"/> — which serves
    /// primary content AND satellites with LIVE deltas under the same access filter — for any
    /// query pinned to a single concrete partition, INCLUDING scoped satellite reads
    /// (<c>namespace:Systemorph/_Thread</c>, <c>nodeType:Thread namespace:Systemorph</c>,
    /// <c>_Access</c>-style globals resolved via the registered-partition cache). This retires
    /// the pedestrian <see cref="StorageAdapterMeshQueryProvider"/> for scoped satellites — it
    /// could only ever walk <c>mesh_nodes</c>, never the satellite tables. Verified by
    /// <c>SatelliteSyncedInitialTests</c>: a permitted user's pre-existing satellite rows land
    /// in the delegate's Initial (the historical "Initial under-returns satellite rows" claim
    /// was the access filter correctly dropping rows for an unpermitted Anonymous caller).
    ///
    /// <para>Two cases still fan out (→ null here): <c>source:activity</c> /
    /// <c>source:accessed</c> are cross-partition satellite JOINs (the dashboard feeds span
    /// every partition — a scoped one still runs on the fan-out's single-element schema list);
    /// and unscoped / wildcard-first paths (<see cref="GetDelegateForPath"/> returns null), the
    /// genuine cross-partition broadcasts.</para>
    /// </summary>
    private PostgreSqlMeshQuery? GetScopedDelegate(ParsedQuery parsed)
        => parsed.Source is QuerySource.Activity or QuerySource.Accessed
            ? null
            : GetDelegateForPath(parsed.Path);

    /// <inheritdoc/>
    /// <remarks>
    /// MeshQuery's fan-in passes every provider every query — this provider
    /// internally short-circuits scoped requests (see
    /// <see cref="NeedsFanOut"/>), so always claim a match. Returning
    /// <see langword="true"/> here is symmetric with
    /// <see cref="StorageAdapterMeshQueryProvider.Matches"/>: the routing
    /// decision lives in <see cref="Query{T}"/>.
    /// </remarks>
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Routes EACH query in <see cref="MeshQueryRequest.EffectiveQueries"/> to the
    /// partition that owns it. Routing on the FIRST query only (the pre-2026-07 shape)
    /// pinned a multi-partition bundle — e.g. a NodeType compile's source queries mixing
    /// <c>namespace:Own/Source</c> with <c>shared=</c> queries against another partition —
    /// to the first query's schema, so every other partition's queries silently matched
    /// zero rows.
    /// </remarks>
    public IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
    {
        var effective = request.EffectiveQueries;
        var routes = new (string Query, ParsedQuery Parsed, PostgreSqlMeshQuery? Scoped)[effective.Count];
        for (var i = 0; i < effective.Count; i++)
        {
            var query = effective[i];
            var parsed = string.IsNullOrEmpty(query) ? ParsedQuery.Empty : _parser.Parse(query);
            routes[i] = (query, parsed, GetScopedDelegate(parsed));
        }

        // SINGLE-TARGET fast path — every query is served by the SAME per-schema
        // PostgreSqlMeshQuery (always the case for a single-query request): hand
        // the ORIGINAL request to that delegate (live deltas + satellite serving
        // over the cached adapter; the delegate pushes the multi-query UNION down
        // to Postgres). This is the scoped serving that retires the pedestrian
        // for Postgres.
        var firstScoped = routes[0].Scoped;
        if (firstScoped is not null && routes.All(r => ReferenceEquals(r.Scoped, firstScoped)))
            return firstScoped.Query<T>(request, options);

        // ALL-FAN-OUT fast path — no query pins to a concrete partition: the whole
        // request is served by the cross-schema fan-out.
        if (routes.All(r => r.Scoped is null))
            return FanOutQuery<T>(request, routes.Select(r => r.Parsed).ToArray(), options);

        // MULTI-PARTITION: group the queries by their owning per-schema delegate and
        // run each group as a sub-request against its own partition. Identity /
        // paging fields (UserId, Context, Skip, Limit, …) flow through unchanged —
        // only the query membership is regrouped. Unpinned queries go to the shared
        // cross-schema fan-out. The group streams are merged under the provider
        // contract MergeProviderObservables (MeshQuery) enforces one level up:
        // exactly ONE Initial, then live deltas.
        var groups = new List<(PostgreSqlMeshQuery Scoped, List<string> Queries)>();
        var unpinned = new List<ParsedQuery>();
        foreach (var (query, parsed, scoped) in routes)
        {
            if (scoped is null)
            {
                unpinned.Add(parsed);
                continue;
            }
            var idx = groups.FindIndex(g => ReferenceEquals(g.Scoped, scoped));
            if (idx < 0) groups.Add((scoped, [query]));
            else groups[idx].Queries.Add(query);
        }

        var sources = new List<IObservable<QueryResultChange<T>>>(groups.Count + 1);
        foreach (var (scoped, queries) in groups)
            sources.Add(scoped.Query<T>(request with { Queries = queries, Query = queries[0] }, options));
        if (unpinned.Count > 0)
            sources.Add(FanOutQuery<T>(request, unpinned, options));

        return MergePartitionStreams(sources, routes[0].Parsed);
    }

    /// <summary>
    /// The cross-schema fan-out for the queries of a request that do NOT pin to a
    /// concrete partition: one Initial snapshot (the union of every fan-out query's
    /// rows, deduped by path) followed by LIVE <c>Added</c> / <c>Updated</c> /
    /// <c>Removed</c> deltas — the same Initial-then-changes contract every other
    /// <see cref="IMeshQueryProvider"/> honours.
    ///
    /// <para>🚨 This used to be ONE-SHOT (Initial, then complete) and that was issue #697 —
    /// a fail-OPEN revocation hole, not a cosmetic gap. <c>PermissionEvaluator</c> resolves a
    /// user's groups from a PATH-LESS synced query (<c>$security-memberships</c> —
    /// <c>nodeType:GroupMembership scope:subtree</c>, path-less because a group and its members
    /// may live in a different partition than the grant). Path-less ⇒
    /// <see cref="GetDelegateForPath"/> returns null ⇒ the whole request lands HERE; meanwhile
    /// <c>StorageAdapterMeshQueryProvider.DefersToNativeProvider</c> returns true for an empty
    /// path so the pedestrian contributes an empty Initial and opens NO change subscription, and
    /// <c>StaticNodeQueryProvider</c> is one-shot by nature. With this method one-shot too, NO
    /// provider held a live subscription: the process-wide snapshot behind
    /// <c>IMeshNodeStreamCache.GetQuery</c> (<c>Replay(1).AutoConnect(1)</c>, never rebuilt) froze
    /// at its Initial for the lifetime of the process. Removing a user from a group therefore
    /// never reached the node-open path — the removed user kept reading protected records until
    /// the portal was restarted — and re-adding them was equally invisible, in both directions.
    /// (Deletes only ever "worked" by accident, through <c>SyncedQueryMeshNodes</c>' un-gated
    /// <c>IMeshChangeFeed</c> Deleted fast-path, which several delete routes bypass and which has
    /// no Created/Updated counterpart.) Every path-less query had the same hole —
    /// <c>$security-roles</c> included.</para>
    ///
    /// <para><b>Cost is bounded by an EXACT relevance gate, not by a debounce.</b> A path-less
    /// query's path/scope predicate (<c>PathMatcher.ShouldNotify</c> with an empty base) matches
    /// EVERY notification, so re-querying on it would run a cross-schema UNION on every write in
    /// the process. Instead <c>IsRelevant</c> decides from the notification itself: a change to a
    /// path already in the result set can alter or remove a row, so it is relevant; otherwise only
    /// a write whose payload actually satisfies the query's own filter
    /// (<see cref="QueryEvaluator.Matches"/> — the same matcher the pedestrian provider uses) can
    /// bring a NEW row in. A delete carries no payload (<c>Entity == null</c>), but a delete can
    /// only ever REMOVE, so "not in the current result set" is a complete answer for it — once
    /// there IS a result set: until the Initial snapshot is established every delete counts as
    /// relevant, because a delete racing the initial read could otherwise leave a just-deleted row
    /// in the Initial with nothing to correct it. Anything we cannot classify (a notification with
    /// no entity that is not a delete) re-queries — under-notifying is a security hole,
    /// over-notifying only costs a query.</para>
    ///
    /// <para><b>The schema list must be current, and that is NOT this method's job.</b> The
    /// cross-schema re-query fans out over <c>public.searchable_schemas</c>. That registry is
    /// written when a partition is PROVISIONED
    /// (<see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>), not
    /// discovered by the throttled sync alone — because a live re-query runs only when a change
    /// arrives, so a partition missing from the list at that instant is not seen late, it is never
    /// seen at all.</para>
    ///
    /// <para>Not completing is the norm, not a change in kind: the per-schema
    /// <see cref="PostgreSqlMeshQuery"/> that serves every SCOPED query never completes either, so
    /// no consumer can already be relying on a query stream terminating. Consumers take the
    /// snapshot with <c>.Where(c =&gt; c.ChangeType == Initial).Take(1)</c>.</para>
    /// </summary>
    private IObservable<QueryResultChange<T>> FanOutQuery<T>(
        MeshQueryRequest request, IReadOnlyList<ParsedQuery> parsedQueries, JsonSerializerOptions options)
    {
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            foreach (var parsed in parsedQueries)
                _logger.LogDebug(
                    "[FanOut] Query decision: NeedsFanOut={NeedsFanOut} Path='{Path}' Source={Source} Query='{Q}'",
                    NeedsFanOut(parsed), parsed.Path ?? "(null)", parsed.Source, request.Query);
        }

        var firstParsed = parsedQueries.Count > 0 ? parsedQueries[0] : ParsedQuery.Empty;

        // MergeProviderObservables in MeshQuery gates the merged Initial on
        // every provider emitting Initial. If we return Observable.Empty when
        // the query is scoped (NeedsFanOut=false) the source completes WITHOUT
        // emitting Initial, the merge counter never increments past our slot,
        // and the consumer hangs forever. Emit an empty Initial in the scoped
        // case so the merge can proceed (the per-schema StorageAdapterMeshQueryProvider
        // is the one that contributes the real rows for that path).
        //
        // COLD by contract: _ioPool.Invoke runs the fan-out on Subscribe (work
        // happens once, when the aggregator subscribes). The previous shape
        // eagerly Subscribe()d into a ReplaySubject at observable-CONSTRUCTION
        // time — running the cross-schema SQL on the calling hub/grain's turn
        // even when the consumer never subscribed, and keeping it running after
        // the consumer unsubscribed. EnumerateFanOutAsync is the private async
        // leaf and is pumped exclusively inside this pool slot. Re-invoked (never
        // replayed) for each relevant change below.
        IObservable<List<(string? Path, T Item)>> RunQuery() => _ioPool.Invoke(async ct =>
        {
            var items = new List<(string? Path, T Item)>();
            // Path-dedupe across the fan-out queries of one request — two branches
            // (e.g. `path:X` + `namespace:X scope:subtree`) can match the same row.
            var seenPaths = parsedQueries.Count > 1
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : null;
            foreach (var parsed in parsedQueries)
            {
                if (!NeedsFanOut(parsed))
                    continue;
                await foreach (var node in EnumerateFanOutAsync(parsed, options, request, ct).ConfigureAwait(false))
                {
                    if (node is not T typed)
                        continue;
                    if (seenPaths is not null
                        && !string.IsNullOrEmpty(node.Path)
                        && !seenPaths.Add(node.Path))
                        continue;
                    items.Add((node.Path, typed));
                }
            }
            return items;
        });

        QueryResultChange<T> Change(QueryChangeType type, IReadOnlyList<T> items) => new()
        {
            ChangeType = type,
            Version = Interlocked.Increment(ref _version),
            Query = firstParsed,
            Items = items,
            Timestamp = DateTimeOffset.UtcNow,
        };

        var feed = _partitionProvider?.MergedChanges;
        if (feed is null)
            // No merged change feed to watch (no partition provider wired) — there is nothing to
            // subscribe to, so keep the historical one-shot rather than pay for a live loop that
            // could never fire.
            return RunQuery().Select(results => Change(
                QueryChangeType.Initial, results.Select(r => r.Item).ToList()));

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            var disposables = new System.Reactive.Disposables.CompositeDisposable();
            // Mutated ONLY inside ApplySnapshot, which the Concat below serialises.
            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            // Lock-free view of the paths currently in the result set: read by IsRelevant on the
            // change-feed's thread, REPLACED wholesale (never mutated) by ApplySnapshot. An
            // immutable set + Volatile keeps the hot filter off any lock.
            var livePaths = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
            // 0 until the Initial snapshot has been established (set alongside `initialDone`).
            // Read by IsRelevant on the feed's thread — Volatile, never a lock on the hot filter.
            var initialEstablished = 0;

            bool IsRelevant(DataChangeNotification n)
            {
                if (!string.IsNullOrEmpty(n.Path) && Volatile.Read(ref livePaths).Contains(n.Path))
                    return true;                      // in the result set — may be updated or removed
                if (n.Kind == DataChangeKind.Deleted)
                    // A delete can only REMOVE, so "not in the current result set" is a complete
                    // answer — but ONLY once there IS a result set. Before the Initial snapshot is
                    // established `livePaths` is still empty, so a delete racing the initial read
                    // would be dropped while the read itself may still have returned the row: the
                    // Initial would carry a row that no longer exists, and nothing would correct it
                    // until some unrelated write happened to re-query. Treat every delete as
                    // relevant until then — it lands in the early backlog and is drained through
                    // the same serialised re-query right after the Initial.
                    return Volatile.Read(ref initialEstablished) == 0;
                if (n.Entity is not MeshNode node)
                    return true;                      // unclassifiable — re-query rather than miss it
                foreach (var parsed in parsedQueries)
                    if (NeedsFanOut(parsed) && _evaluator.Matches(node, parsed))
                        return true;                  // a NEW row could enter the result set
                return false;
            }

            void ApplySnapshot(List<(string? Path, T Item)> newResults)
            {
                var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, item) in newResults)
                    if (!string.IsNullOrEmpty(path))
                        newItems[path] = item;

                var added = new List<T>();
                var updated = new List<T>();
                var removed = new List<T>();
                foreach (var (path, item) in newItems)
                {
                    if (currentItems.TryGetValue(path, out var existing))
                    {
                        if (!ItemEquals(existing, item))
                            updated.Add(item);
                    }
                    else
                    {
                        added.Add(item);
                    }
                }
                foreach (var (path, item) in currentItems)
                    if (!newItems.ContainsKey(path))
                        removed.Add(item);

                currentItems.Clear();
                foreach (var (p, v) in newItems)
                    currentItems[p] = v;
                Volatile.Write(ref livePaths,
                    newItems.Keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

                if (added.Count > 0) observer.OnNext(Change(QueryChangeType.Added, added));
                if (updated.Count > 0) observer.OnNext(Change(QueryChangeType.Updated, updated));
                if (removed.Count > 0) observer.OnNext(Change(QueryChangeType.Removed, removed));
            }

            // Subscribe to the feed BEFORE the initial query so a write landing during the
            // fan-out's I/O window is captured rather than dropped (the merged feed is a plain
            // Subject with no buffering) — same race-fix as PostgreSqlMeshQuery / the pedestrian.
            var earlyBacklog = new List<DataChangeNotification>();
            var earlyLock = new object();
            var initialDone = false;
            var earlySubscription = feed.Where(IsRelevant).Subscribe(n =>
            {
                lock (earlyLock)
                {
                    if (!initialDone)
                        earlyBacklog.Add(n);
                }
            });
            disposables.Add(earlySubscription);

            disposables.Add(RunQuery().Subscribe(
                initialResults =>
                {
                    var initialItems = new List<T>(initialResults.Count);
                    foreach (var (path, item) in initialResults)
                    {
                        initialItems.Add(item);
                        if (!string.IsNullOrEmpty(path))
                            currentItems[path] = item;
                    }
                    Volatile.Write(ref livePaths,
                        currentItems.Keys.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

                    // 🚨 TWO producers write this buffer — the live feed subscription below AND
                    // the backlog drain scheduled after the Initial — so it must be SYNCHRONIZED.
                    // A plain Subject delivers OnNext straight through on the calling thread with
                    // no serialisation; two concurrent OnNext calls interleave inside the observer
                    // list and can corrupt delivery. Subject.Synchronize is the same wrapper the
                    // other multi-producer subjects in the codebase use (MeshNodeStreamCache).
                    // The INNER subject is what gets disposed — disposing the synchronized facade
                    // is not the same object.
                    var innerBuffer = new System.Reactive.Subjects.Subject<DataChangeNotification>();
                    var changeBuffer = System.Reactive.Subjects.Subject.Synchronize(innerBuffer);
                    // 🚨 ORDER MATTERS. CompositeDisposable disposes in INSERTION order, so the
                    // feeding subscription must be registered BEFORE the buffer it writes into —
                    // the other way round, teardown disposes changeBuffer while the subscription
                    // is still live and a write in that window calls OnNext on a disposed Subject
                    // (the ObjectDisposedException that starved the $security-access fold, #889).
                    disposables.Add(feed.Where(IsRelevant).Subscribe(changeBuffer));
                    disposables.Add(innerBuffer);
                    // One re-query per relevant change, serialised via Concat so currentItems is
                    // never raced. No debounce: a batching window is exactly the gap in which a
                    // subscriber attaching mid-flush sees the pre-write snapshot.
                    disposables.Add(changeBuffer
                        .Select(_ => RunQuery())
                        .Concat()
                        .Subscribe(ApplySnapshot, ex => observer.OnError(ex)));

                    DataChangeNotification[] backlog;
                    lock (earlyLock)
                    {
                        backlog = earlyBacklog.ToArray();
                        earlyBacklog.Clear();
                        initialDone = true;
                        // Same instant, for IsRelevant's lock-free read: from here on a delete is
                        // classified against the (now existing) result set instead of blanket-kept.
                        Volatile.Write(ref initialEstablished, 1);
                    }
                    earlySubscription.Dispose();

                    observer.OnNext(Change(QueryChangeType.Initial, initialItems));

                    // Drain the backlog through the same serialised pipeline rather than a
                    // parallel re-query that would race the first live batch.
                    if (backlog.Length > 0)
                    {
                        Scheduler.Default.Schedule(() =>
                        {
                            foreach (var n in backlog)
                            {
                                if (disposables.IsDisposed) return;
                                try { changeBuffer.OnNext(n); }
                                catch (ObjectDisposedException) { return; }
                            }
                        });
                    }
                },
                ex => observer.OnError(ex)));

            return disposables;
        });
    }

    /// <summary>
    /// Equality for the live diff above. For a <see cref="MeshNode"/> the volatile / non-
    /// serializable fields are stripped first — <c>HubConfiguration</c> and
    /// <c>GlobalServiceConfigurations</c> hold <c>Func&lt;&gt;</c>s that never compare equal, and
    /// <c>LastModified</c>/<c>Version</c> change on every write — so a re-query that returned the
    /// same content does not masquerade as an Updated. Mirrors
    /// <c>StorageAdapterMeshQueryProvider.ItemEquals</c> and its <see cref="PostgreSqlMeshQuery"/>
    /// twin; the three diff loops are candidates for a shared helper.
    /// </summary>
    private static bool ItemEquals<T>(T a, T b)
    {
        if (a is MeshNode nodeA && b is MeshNode nodeB)
            return nodeA with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 }
                == nodeB with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 };
        return Equals(a, b);
    }

    /// <summary>
    /// Merges the per-partition group streams of a multi-partition request into the
    /// single Initial-then-changes contract this provider owes the aggregator —
    /// mirroring <c>MeshQuery.MergeProviderObservables</c> one level down: ONE
    /// combined Initial (items path-deduped across groups, scores preserved) is
    /// emitted only after EVERY group has contributed its Initial; subsequent live
    /// deltas from the per-schema delegates flow through unchanged. A group that
    /// COMPLETES without an Initial (contract violation) is counted as an empty
    /// Initial — the same completion guard the aggregator applies — so one silent
    /// group can never starve the merge.
    /// </summary>
    /// <param name="sources">One stream per partition group (plus at most one fan-out stream).</param>
    /// <param name="firstParsed">The FIRST query's parse — stamped on the merged
    /// Initial because a multi-query union takes sort/limit/skip from the first
    /// query (<see cref="MeshQueryRequest.Queries"/> contract); the aggregator's
    /// <c>ClipMergedInitial</c> reads it from <see cref="QueryResultChange{T}.Query"/>.</param>
    private IObservable<QueryResultChange<T>> MergePartitionStreams<T>(
        IReadOnlyList<IObservable<QueryResultChange<T>>> sources,
        ParsedQuery firstParsed)
    {
        if (sources.Count == 1)
            return sources[0];

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            var gate = new object();
            var groupItems = new List<(T Item, double Score)>[sources.Count];
            for (var i = 0; i < groupItems.Length; i++)
                groupItems[i] = new List<(T, double)>();
            var initialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var initialSeen = new bool[sources.Count];
            var initialCount = 0;

            // Emits the merged Initial once every group reported in. Must be called under `gate`.
            void EmitMergedInitialIfComplete()
            {
                if (initialCount != sources.Count)
                    return;
                var items = new List<T>();
                var scores = new List<double>();
                foreach (var hits in groupItems)
                {
                    foreach (var (item, score) in hits)
                    {
                        items.Add(item);
                        scores.Add(score);
                    }
                }
                observer.OnNext(new QueryResultChange<T>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = items,
                    Scores = scores,
                    Query = firstParsed,
                    Version = Interlocked.Increment(ref _version),
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            var subscriptions = new System.Reactive.Disposables.CompositeDisposable();
            for (var i = 0; i < sources.Count; i++)
            {
                var idx = i;
                subscriptions.Add(sources[idx].Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                var changeScores = change.Scores;
                                for (var j = 0; j < change.Items.Count; j++)
                                {
                                    var item = change.Items[j];
                                    // Dedupe by path across groups — a fan-out branch
                                    // spans every schema and can re-match a row a
                                    // pinned group already contributed.
                                    if (item is MeshNode node
                                        && !string.IsNullOrEmpty(node.Path)
                                        && !initialPaths.Add(node.Path))
                                        continue;
                                    var score = changeScores is not null && j < changeScores.Count
                                        ? changeScores[j]
                                        : 0.0;
                                    groupItems[idx].Add((item, score));
                                }
                                if (!initialSeen[idx])
                                {
                                    initialSeen[idx] = true;
                                    initialCount++;
                                }
                                EmitMergedInitialIfComplete();
                            }
                        }
                        else
                        {
                            // Live delta from a per-schema delegate. Partitions are
                            // disjoint, so no cross-group dedup is needed; the
                            // aggregator's live filter handles cross-PROVIDER overlap.
                            lock (gate)
                            {
                                observer.OnNext(change);
                            }
                        }
                    },
                    ex => observer.OnError(ex),
                    () =>
                    {
                        lock (gate)
                        {
                            if (initialSeen[idx])
                                return;
                            _logger?.LogWarning(
                                "Partition group {Index} of a multi-partition query completed WITHOUT "
                                + "emitting an Initial — contract violation; counting as empty so the "
                                + "merged query can proceed. Every Query<T> observable must emit exactly "
                                + "one Initial.",
                                idx);
                            initialSeen[idx] = true;
                            initialCount++;
                            EmitMergedInitialIfComplete();
                        }
                    }));
            }
            return subscriptions;
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Autocomplete NEVER fans out across partitions. Two shapes:
    /// <list type="bullet">
    ///   <item><b>TOP-LEVEL</b> — empty base (root) or an explicit <c>/name</c> prefix → the
    ///     partition roots from the <c>public.top_level_index</c> matview (one small indexed
    ///     relation, PG-scored).</item>
    ///   <item><b>WITHIN-PARTITION</b> — a concrete <paramref name="basePath"/> → owned by the
    ///     per-schema provider (a scoped <c>namespace:&lt;basePath&gt; scope:descendants</c>
    ///     query against the single context schema); this provider contributes nothing.</item>
    /// </list>
    /// </remarks>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
    {
        var isTopLevel = string.IsNullOrEmpty(basePath) || (prefix?.StartsWith('/') ?? false);
        if (!isTopLevel)
            // WITHIN-PARTITION → delegate to the per-schema provider for the context partition
            // (scoped, never fans out). This is the within-partition half the pedestrian served.
            return GetDelegateForPath(basePath)?.Autocomplete(basePath, prefix ?? "", options, mode, limit, contextPath, context)
                ?? Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        var effectivePrefix = (prefix ?? "").TrimStart('/');
        var ctxUser = _accessService?.Context?.ObjectId ?? _accessService?.CircuitContext?.ObjectId;
        // System → null (sees all); no context → Anonymous (Public only); else the caller.
        var effectiveUserId = ctxUser == WellKnownUsers.System ? null
            : string.IsNullOrEmpty(ctxUser) ? WellKnownUsers.Anonymous : ctxUser;

        // _ioPool.Invoke already offloads to the ThreadPool behind its gate — no extra SubscribeOn.
        return _ioPool.Invoke(ct =>
            _crossSchema.AutocompleteTopLevelAsync(effectivePrefix, effectiveUserId, limit, ct));
    }

    /// <inheritdoc/>
    /// <remarks>Single-path Select is scoped → delegate to the per-schema
    /// <see cref="PostgreSqlMeshQuery"/> for the path's partition (cached adapter).</remarks>
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => GetDelegateForPath(path)?.Select<T>(path, property, options)
            ?? Observable.Return<T?>(default);

    /// <summary>
    /// True when the query is genuinely unscoped (no partition-narrowing
    /// constraint), so the result must come from every searchable partition
    /// rather than the caller's own.
    ///
    /// <para>Scoped = a single concrete first segment in <see cref="ParsedQuery.Path"/>
    /// (no <c>*</c>, no empty, no comma/pipe-list across partitions). Wildcards
    /// (<c>*/_Thread</c>) are treated as fan-out because the user is explicitly
    /// asking for results across partitions.</para>
    /// </summary>
    internal static bool NeedsFanOut(ParsedQuery parsed)
    {
        // source:activity / source:accessed are satellite-join queries that the
        // pedestrian StorageAdapterMeshQueryProvider can't handle on Postgres:
        // ListChildPaths only sees mesh_nodes rows, never satellite-table rows,
        // so its subtree walk misses every `_Activity` / `_UserActivity` path.
        // Route them through THIS provider regardless of scope — when the query
        // is namespace-scoped the cross-schema UNION still emits the right
        // rows; it just runs against a one-element schema list.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed)
            return true;
        // Any query that resolves to a SATELLITE table (Thread, Activity,
        // Comment, …) — whether via path segment or nodeType filter — must
        // go through this provider, because the pedestrian StorageAdapterMeshQueryProvider's
        // path walk never visits satellite tables. Without this, even a
        // single-partition `namespace:partition/*/_Thread` query degrades to
        // empty.
        if (ResolveTable(parsed) != "mesh_nodes")
            return true;
        // No Path → unscoped → fan out.
        if (string.IsNullOrEmpty(parsed.Path))
            return true;
        var firstSegment = FirstSegment(parsed.Path);
        // Wildcard first segment → explicit cross-partition request.
        if (firstSegment == "*")
            return true;
        // Single concrete partition → leave to the per-schema provider.
        return false;
    }

    private static string FirstSegment(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return "";
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    private async IAsyncEnumerable<MeshNode> EnumerateFanOutAsync(
        ParsedQuery parsed,
        JsonSerializerOptions options,
        MeshQueryRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        // For source:activity, the satellite to JOIN is "activities" — the
        // primary projection still comes from mesh_nodes (the main content
        // node). For source:accessed, the same shape uses user_activities.
        // Otherwise: ResolveTable picks between path-based satellite mapping
        // (namespace:*/_Thread → threads) and nodeType-based mapping
        // (nodeType:Thread → threads, nodeType:ThreadMessage → threads, …).
        string tableName;
        string? joinTable = null;
        if (parsed.Source is QuerySource.Activity)
        {
            tableName = "mesh_nodes";
            joinTable = "activities";
        }
        else if (parsed.Source is QuerySource.Accessed)
        {
            tableName = "mesh_nodes";
            joinTable = "user_activities";
        }
        else
        {
            tableName = ResolveTable(parsed);
        }

        // 🚨 Partition-pinned fast path. When the parsed query carries a
        // concrete first segment (e.g. `nodeType:Thread namespace:Systemorph`,
        // `path:Systemorph/_Thread/foo`, `namespace:Systemorph/_Thread`), we
        // KNOW which schema to hit — skip the fan-out machinery entirely:
        //   * no `SyncSearchableSchemasAsync` PG round-trip
        //   * no `GetSchemasWithTableAsync` PG round-trip
        //   * single-element schema list straight to QueryAcrossSchemasAsync
        // The two skipped round-trips alone were costing 200-700 ms on cold
        // page loads (visible in Grafana/Loki as serial `SELECT … FROM
        // public.searchable_schemas` and `information_schema.tables` lookups
        // right before the actual UNION query). Trusts that satellite tables
        // exist in every partition that has primary `mesh_nodes` — if a row
        // lookup misses it returns no rows; UNION over zero rows is still
        // correct.
        var pinned = ResolvePinnedPartition(parsed, ResolveGlobalSchema);
        // Routing-rule fallback: a path-less query (no concrete first segment) that matches a
        // registered QueryRoutingRule pins to that rule's partition — nodeType:User → auth,
        // nodeType:Invitation → admin. These schemas are DELIBERATELY excluded from cross-schema
        // search (PostgreSqlCrossSchemaQueryProvider.ExcludedSchemas: auth is the central user/auth
        // mirror, admin is platform-only), so without this a path-less nodeType:User query fans out,
        // misses auth, and never finds the user → the onboarding redirect loop. This realises the
        // hint UserNodeType/InvitationNodeType register (previously inert — no consumer).
        if (pinned is null && _meshConfiguration is not null)
        {
            var hintPartition = _meshConfiguration.ResolveRoutingHints(parsed).Partition;
            if (!string.IsNullOrEmpty(hintPartition))
                pinned = hintPartition.ToLowerInvariant();
        }
        List<string> schemas;
        if (pinned is not null)
        {
            schemas = [pinned];
            _logger?.LogDebug(
                "[FanOut] pinned fast-path: partition={Partition} table={Table} joinTable={JoinTable}",
                pinned, tableName, joinTable ?? "(none)");
        }
        else
        {
            // Unpinned (wildcard or missing first segment) → genuine cross-schema
            // fan-out. Sync the schema list + filter to schemas that contain
            // BOTH the projection table and the join table. Older partitions /
            // static-mesh schemas (Doc, etc.) only ship mesh_nodes — including
            // them in a satellite UNION raises "relation does not exist".
            await _crossSchema.SyncSearchableSchemasAsync(ct).ConfigureAwait(false);
            schemas = (await _crossSchema.GetSchemasWithTableAsync(tableName, ct).ConfigureAwait(false)).ToList();
            // source:accessed joins the ONE user_activities table in the CALLER's schema (see
            // PostgreSqlCrossSchemaQueryProvider) — branch schemas only need mesh_nodes, so no
            // join-table intersection: filtering to schemas that own a user_activities table
            // would drop every static/space partition from the cross-partition accessed feed.
            if (joinTable is not null && parsed.Source != QuerySource.Accessed)
            {
                var withJoin = await _crossSchema.GetSchemasWithTableAsync(joinTable, ct).ConfigureAwait(false);
                var joinSet = new HashSet<string>(withJoin, StringComparer.OrdinalIgnoreCase);
                schemas = schemas.Where(s => joinSet.Contains(s)).ToList();
            }
        }
        _logger?.LogDebug(
            "[FanOut] schemas: count={Count} table={Table} joinTable={JoinTable} pinned={Pinned} list=[{Schemas}]",
            schemas.Count, tableName, joinTable ?? "(none)", pinned ?? "(none)", string.Join(", ", schemas));
        if (schemas.Count == 0)
            yield break;

        // Strip the path when it carries a wildcard segment ("*"). The schema
        // selection + satellite table selection above already encoded the
        // routing intent — keeping `*` in the SQL WHERE clause would force
        // `n.path LIKE '*/...'` which matches nothing. For partition-pinned
        // wildcards (e.g. `namespace:p/*/_Thread`) the partition schema is
        // already bound so every row in `p.threads` matches the pattern.
        var queryForSql = parsed;
        if (!string.IsNullOrEmpty(queryForSql.Path) && queryForSql.Path.Contains('*'))
            queryForSql = queryForSql with { Path = null, Scope = QueryScope.Exact };

        var userId = GetEffectiveUserId(request);
        // activityUserId is only meaningful for source:accessed today (joins
        // user_activities); source:activity reads the activity satellite,
        // not user-scoped.
        var activityUserId = parsed.Source == QuerySource.Accessed ? userId : null;

        _logger?.LogInformation(
            "[FanOut] schemas={Count} table={Table} source={Source} userId={User} query={Q}",
            schemas.Count, tableName, parsed.Source, userId, request.Query);

        await foreach (var node in _crossSchema.QueryAcrossSchemasAsync(
                            queryForSql, options, schemas, tableName,
                            userId == WellKnownUsers.System ? null : userId,
                            activityUserId, ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    /// <summary>
    /// Resolves the satellite table the fan-out should UNION across, given a
    /// parsed query. Consulted in priority order:
    /// <list type="number">
    ///   <item><b>Path segment</b> — <c>namespace:*/_Thread</c>,
    ///     <c>namespace:partition/_Thread</c>, <c>namespace:partition/*/_Thread</c>,
    ///     and any other path that carries a satellite segment
    ///     (<c>_Thread</c>, <c>_ThreadMessage</c>, <c>_Activity</c>,
    ///     <c>_Access</c>, …) resolve via <see cref="SatelliteTableMapping"/>.
    ///     Longest-suffix-wins ordering inside <see cref="PartitionDefinition.ResolveTable"/>
    ///     means <c>_ThreadMessage</c> beats <c>_Thread</c> when both could match.</item>
    ///   <item><b>nodeType filter</b> — when the path is missing or doesn't
    ///     contain a satellite segment, fall back to the nodeType filter:
    ///     <c>nodeType:Thread</c> → <c>threads</c>, <c>nodeType:ThreadMessage</c> → <c>threads</c>,
    ///     <c>nodeType:Activity</c> → <c>activities</c>, <c>nodeType:Comment</c> → <c>annotations</c>,
    ///     etc. — via <see cref="SatelliteTableMapping"/> chained
    ///     into <see cref="SatelliteTableMapping"/>.</item>
    ///   <item><b>Fallback</b> — <c>mesh_nodes</c> for primary content.</item>
    /// </list>
    /// </summary>
    internal static string ResolveTable(ParsedQuery parsed)
    {
        // Satellite table layout — the configurable default (PostgreSqlStorageOptions.SatelliteTables);
        // table names are uniform across a fan-out's schemas, so the default maps apply. No static dict.
        var segmentTables = PartitionDefinition.DefaultSegmentTableMappings();

        // Path-based mapping first — a concrete path with a satellite segment
        // (e.g. namespace:partition/doc/_Thread) pins the satellite table.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            foreach (var (suffix, table) in segmentTables.OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(parsed.Path, suffix))
                    return table;
            }
        }

        // Wildcard namespace mapping — `namespace:*/_Thread` is parsed as a
        // `namespace LIKE '%/_Thread'` filter (NOT as a Path), so the
        // path-based check above doesn't see it. Walk the parsed filter for
        // a namespace LIKE node and inspect its value for a satellite segment.
        var nsLikeValue = ExtractNamespaceLikeValue(parsed.Filter);
        if (!string.IsNullOrEmpty(nsLikeValue))
        {
            // Strip SQL wildcards so PathContainsSegment can do its
            // boundary check ("partition/%/_Thread" → "partition//_Thread"
            // → still has '_Thread' bounded by '/').
            var sanitized = nsLikeValue.Replace("%", "");
            foreach (var (suffix, table) in segmentTables.OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(sanitized, suffix))
                    return table;
            }
        }

        // nodeType-based mapping when neither path nor namespace LIKE
        // carries a satellite hint.
        var nodeType = parsed.ExtractNodeType();
        if (!string.IsNullOrEmpty(nodeType)
            && PartitionDefinition.DefaultNodeTypeTableMappings().TryGetValue(nodeType, out var nodeTypeTable))
        {
            return nodeTypeTable;
        }

        return "mesh_nodes";
    }

    /// <summary>
    /// Walks <paramref name="node"/> for a <c>QueryComparison</c> whose
    /// selector is <c>namespace</c> and operator is <c>Like</c>. The
    /// <see cref="QueryParser"/> emits exactly this shape for
    /// <c>namespace:VALUE_WITH_*</c> (e.g. <c>namespace:*/_Thread</c>) —
    /// stashing the matched pattern as the LIKE argument. Returns the raw
    /// pattern (with <c>%</c> still in place) for the caller to sanitise.
    /// <see langword="null"/> if no matching node.
    /// </summary>
    private static string? ExtractNamespaceLikeValue(QueryNode? node)
    {
        if (node is null) return null;
        if (node is QueryComparison c
            && c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
            && c.Condition.Operator == QueryOperator.Like
            && c.Condition.Values is { Length: > 0 } values)
        {
            return values[0];
        }
        if (node is QueryAnd andNode)
        {
            foreach (var child in andNode.Children)
            {
                var v = ExtractNamespaceLikeValue(child);
                if (v is not null) return v;
            }
        }
        if (node is QueryOr orNode)
        {
            foreach (var child in orNode.Children)
            {
                var v = ExtractNamespaceLikeValue(child);
                if (v is not null) return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the partition scope of <paramref name="parsed"/>:
    /// <list type="bullet">
    ///   <item>A concrete first segment (e.g. <c>namespace:partition/...</c>,
    ///     <c>namespace:partition/*/_Thread</c>) pins the query to ONE
    ///     partition; the fan-out narrows to that single schema.</item>
    ///   <item><c>*</c> as first segment or an empty/missing path → fan out
    ///     across every searchable partition.</item>
    /// </list>
    /// </summary>
    internal static string? ResolvePinnedPartition(
        ParsedQuery parsed, Func<string, string?>? resolveGlobalSchema = null)
    {
        // Concrete Path wins — e.g. `namespace:partition/doc/_Thread` lands here.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            var first = FirstSegment(parsed.Path);
            if (string.IsNullOrEmpty(first) || first == "*") return null;
            // 🚨 GLOBAL SATELLITE NAMESPACES (`_Access`, `_Activity`,
            // `_UserActivity`, `_Thread`) are registered with explicit Schema
            // names (`system_access`, `system_activity`, …) — the schema is NOT
            // the lowercased namespace. Resolve them through the registered-
            // partition lookup (the SAME source PostgreSqlPathRoutingAdapter.ResolveSchema
            // uses for scoped reads) so the fan-out pins to the one real schema
            // instead of a SyncSearchableSchemas + GetSchemasWithTableAsync discovery
            // round-trip. Unregistered (or no resolver) → null → fan-out (correctness floor).
            if (first.StartsWith('_')) return resolveGlobalSchema?.Invoke(first);
            return first.ToLowerInvariant();
        }

        // Wildcard namespace path went into a LIKE filter — e.g.
        // `namespace:partition/*/_Thread` parses as `namespace LIKE 'partition/%/_Thread'`.
        // If the FIRST segment of the LIKE pattern is concrete (no '*' / '%'),
        // pin to that partition.
        var nsLike = ExtractNamespaceLikeValue(parsed.Filter);
        if (!string.IsNullOrEmpty(nsLike))
        {
            // Find the first segment of the pattern (everything before the
            // first '/' or '%'). If it has no wildcard, it's the partition.
            var trimmed = nsLike.TrimStart('/');
            var stopIdx = trimmed.IndexOfAny(new[] { '/', '%', '*' });
            var first = stopIdx < 0 ? trimmed : trimmed[..stopIdx];
            if (!string.IsNullOrEmpty(first) && !first.Contains('*') && !first.Contains('%'))
            {
                // Same global-satellite resolution as the path-based branch.
                if (first.StartsWith('_')) return resolveGlobalSchema?.Invoke(first);
                return first.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors <see cref="PartitionDefinition"/>'s private path-segment match:
    /// the suffix must appear at a path boundary (either start-of-string or
    /// preceded by '/' AND followed by '/' or end-of-string).
    /// </summary>
    private static bool PathContainsSegment(string path, string segment)
    {
        var idx = 0;
        while (idx < path.Length)
        {
            var pos = path.IndexOf(segment, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            var atStart = pos == 0 || path[pos - 1] == '/';
            var atEnd = pos + segment.Length == path.Length || path[pos + segment.Length] == '/';
            if (atStart && atEnd) return true;
            idx = pos + 1;
        }
        return false;
    }

    /// <summary>
    /// Resolves a <c>_</c>-prefixed global-satellite namespace (<c>_Access</c>,
    /// <c>_Activity</c>, <c>_Thread</c>, <c>_UserActivity</c>) to its real partition
    /// schema (<c>system_access</c>, …) via the process-wide registered-partition cache —
    /// the same lookup <see cref="PostgreSqlPathRoutingAdapter"/> uses for scoped reads.
    /// Lets the fan-out pin these to the one real schema instead of an information_schema
    /// discovery round-trip. <see langword="null"/> when no provider is wired or the
    /// namespace isn't registered (→ caller falls back to the discovery fan-out).
    /// </summary>
    private string? ResolveGlobalSchema(string segment)
        => _partitionProvider is not null
           && _partitionProvider.TryGetRegisteredPartition(segment, out var def)
           && !string.IsNullOrEmpty(def.Schema)
            ? def.Schema
            : null;

    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        if (request.UserId == WellKnownUsers.System)
            return WellKnownUsers.System;
        if (!string.IsNullOrEmpty(request.UserId))
            return request.UserId;
        var userId = _accessService?.Context?.ObjectId
                     ?? _accessService?.CircuitContext?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }
}
