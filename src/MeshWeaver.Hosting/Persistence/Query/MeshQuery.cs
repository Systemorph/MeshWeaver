using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Single top-level query fan-out. Aggregates every registered
/// <see cref="IMeshQueryProvider"/> for both the secured surface
/// (<see cref="Query{T}(MeshQueryRequest)"/>) and the unsecured
/// <see cref="IMeshQueryCore"/> surface (used by SyncedQueryMeshNodes /
/// SecurityService to dodge the validator cycle). One boss for fan-out —
/// per-adapter providers stay leaves.
/// <para>
/// source:activity implies nodeType:Activity filter; source:accessed JOINs with UserActivity
/// nodes to order by last-access time. Providers that don't support these sources return normal results.
/// Identity is resolved from AccessService.Context. Use accessService.ImpersonateAsHub(hub)
/// to temporarily switch identity for hub-level operations.
/// </para>
/// </summary>
public class MeshQuery : IMeshQueryCore
{
    private readonly IReadOnlyList<IMeshQueryProvider> providers;
    private readonly IMessageHub hub;

    // The tracked, DRAINABLE pool the query SUBSCRIBE runs through (replaces a bare
    // .SubscribeOn(TaskPoolScheduler.Default) the teardown drain couldn't reach). Running the
    // subscribe here means teardown's IoPoolRegistry.DrainAll() cancel+joins it, so a query
    // straggler can't create a per-node hub on the disposing Autofac scope (the teardown SIGSEGV).
    private MeshWeaver.Mesh.Threading.IIoPool? _queryPool;
    private MeshWeaver.Mesh.Threading.IIoPool QueryPool => _queryPool ??=
        hub?.ServiceProvider?.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>()
            ?.Get(MeshWeaver.Mesh.Threading.IoPoolNames.Query)
        ?? MeshWeaver.Mesh.Threading.IoPool.Unbounded;

    /// <summary>
    /// Creates the top-level query fan-out, deduplicating the registered
    /// providers by <see cref="IMeshQueryProvider.Name"/> so duplicate
    /// registrations of the same provider class execute only once per query.
    /// </summary>
    /// <param name="providers">The registered per-adapter query providers to aggregate.</param>
    /// <param name="hub">The message hub supplying JSON serializer options and identity context.</param>
    public MeshQuery(IEnumerable<IMeshQueryProvider> providers, IMessageHub hub)
    {
        // Distinct by Name — multiple AddSingleton<IMeshQueryProvider>(factory)
        // calls for the same provider class register duplicates that
        // TryAddEnumerable can't dedupe (factories have null ImplementationType).
        // Names default to the provider's type FullName, so the duplicate
        // StaticNodeQueryProvider registrations from AddPersistence vs
        // AddCoreAndWrapperServices fold into one execution per query.
        this.providers = providers
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        this.hub = hub;
    }

    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    private ILogger? _logger;

    // Defensive: the unsecured IMeshQueryCore registration constructs MeshQuery with hub: null
    // (options are passed per call there), so the logger resolves lazily and tolerates absence.
    private ILogger? Logger
    {
        get
        {
            if (_logger is not null) return _logger;
            try
            {
                return _logger = hub?.ServiceProvider?.GetService<ILoggerFactory>()
                    ?.CreateLogger<MeshQuery>();
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Pre-extracts the partition candidates a query targets — union of
    /// <c>namespace:</c> condition values and the first segment of
    /// <see cref="ParsedQuery.Path"/>. Computed once per query and passed
    /// to every <see cref="IMeshQueryProvider.Matches"/> call.
    /// </summary>
    internal static IReadOnlyList<string> MergeQueryNamespaces(ParsedQuery parsed)
    {
        var fromFilter = parsed.ExtractNamespaces();
        if (string.IsNullOrEmpty(parsed.Path))
            return fromFilter;
        var firstSegment = parsed.Path.Split('/', 2)[0];
        if (fromFilter.Count == 0)
            return new[] { firstSegment };
        var combined = new List<string>(fromFilter.Count + 1);
        combined.AddRange(fromFilter);
        if (!combined.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            combined.Add(firstSegment);
        return combined;
    }

    /// <summary>
    /// Secured fan-out: runs the request against every registered provider
    /// through the public (access-controlled) surface and merges their
    /// emissions into a single delta stream — one combined Initial frame,
    /// then forwarded Added / Updated / Removed changes — deduplicated by
    /// node path.
    /// </summary>
    /// <typeparam name="T">The result element type (typically <c>MeshNode</c>).</typeparam>
    /// <param name="request">The query request carrying filters, path, scope, paging, and identity.</param>
    /// <returns>An observable of merged query result changes across all matching providers.</returns>
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request)
    {
        var matched = SelectMatchingProviders(NamespacesForRequest(request));
        // Run the subscribe on the DRAINABLE Query pool (not a bare TaskPoolScheduler): keeps the
        // calling hub's action block free while providers open their change feeds, AND makes the
        // subscribe tracked so teardown's DrainAll cancel+joins it before the scope is disposed.
        return QueryPool.SubscribeThroughPool(
            MergeProviderObservables(
                matched.Select(p => (p.Query<T>(request, Options), p.Name)).ToList(),
                request));
    }

    /// <summary>
    /// 🚨 NEW unified surface — each <see cref="IMeshQueryProvider"/> emits
    /// snapshots of <see cref="QueryResult"/> rows; we combine via
    /// <see cref="Observable.CombineLatest{TSource}(IEnumerable{IObservable{TSource}})"/>,
    /// dedupe by <see cref="QueryResult.Path"/> (highest-score wins; provider
    /// name as final tiebreak), and re-emit on every change. Providers run
    /// their async I/O inside their own hosted hubs — the call here never
    /// touches the mesh hub's action block.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Query(MeshQueryRequest request)
    {
        var matched = SelectMatchingProviders(NamespacesForRequest(request));
        if (matched.Count == 0)
            return Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());
        IReadOnlyCollection<QueryResult> empty = Array.Empty<QueryResult>();
        // .StartWith(empty) per provider so CombineLatest emits as soon as ANY
        // source converges instead of stalling on the slowest: source B's rows
        // render immediately (the other sources contribute their empty seed),
        // then the snapshot re-emits — re-merged + re-ordered by score — as A and
        // C return. Same progressive shape as Autocomplete; the brief leading
        // all-empty frame is the cost of not waiting for the whole fan-out.
        var streams = matched.Select(p => p.Query(request, Options).StartWith(empty)).ToList();
        // Subscribe on the DRAINABLE Query pool (see Query<T>): off the calling hub's action block
        // AND tracked, so teardown's DrainAll cancel+joins the subscribe before the scope disposes.
        return QueryPool.SubscribeThroughPool(
            Observable.CombineLatest(streams)
                .Select(snapshots => MergeSnapshots(snapshots)));
    }

    /// <summary>
    /// 🚨 NEW unified autocomplete — same shape as <see cref="Query"/> but with
    /// <c>.StartWith(empty)</c> per provider so <see cref="Observable.CombineLatest{TSource}(IEnumerable{IObservable{TSource}})"/>
    /// emits as soon as ANY provider produces. Slow providers don't gate the
    /// UI — partial autocomplete suggestions render immediately.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
    {
        var matched = SelectMatchingProviders(NamespacesForBasePath(basePath));
        if (matched.Count == 0)
            return Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());
        IReadOnlyCollection<QueryResult> empty = Array.Empty<QueryResult>();
        var streams = matched.Select(p => p
            .Autocomplete(basePath, prefix, Options, mode, limit, contextPath, context)
            .StartWith(empty));
        return Observable.CombineLatest(streams)
            .Select(snapshots => MergeAutocompleteSnapshots(snapshots, limit, contextPath, prefix));
    }

    private static IReadOnlyCollection<QueryResult> MergeSnapshots(IList<IReadOnlyCollection<QueryResult>> snapshots)
    {
        var byPath = new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var row in snapshot)
            {
                if (string.IsNullOrEmpty(row.Path)) continue;
                if (byPath.TryGetValue(row.Path, out var existing) && existing.Score >= row.Score)
                    continue;
                byPath[row.Path] = row;
            }
        }
        return byPath.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path.Length)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyCollection<QueryResult> MergeAutocompleteSnapshots(
        IList<IReadOnlyCollection<QueryResult>> snapshots, int limit, string? contextPath, string? prefix)
    {
        var byPath = new Dictionary<string, QueryResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var row in snapshot)
            {
                if (string.IsNullOrEmpty(row.Path)) continue;
                // Keep satellite NOISE (AccessAssignment grants, Thread/Comment cells, …) out of
                // autocomplete — by the node's STORAGE table (configured satellite SEGMENT), NOT the raw
                // '_' character: {ns}/_Policy, {ns}/_Provider have a '_' segment yet are NOT configured
                // satellite segments, so they live in mesh_nodes and are real content the user should be
                // able to autocomplete to. Satellite-ness is configuration (SatelliteTableMapping).
                if (SatelliteTableMapping.IsSatellitePath(row.Path)) continue;
                if (byPath.TryGetValue(row.Path, out var existing) && existing.Score >= row.Score)
                    continue;
                var boosted = string.IsNullOrEmpty(contextPath)
                    ? row
                    : ApplyProximityBoost(row, contextPath, prefix);
                byPath[row.Path] = boosted;
            }
        }
        return byPath.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path.Length)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static QueryResult ApplyProximityBoost(QueryResult row, string? contextPath, string? prefix)
    {
        if (string.IsNullOrEmpty(contextPath)) return row;
        var path = row.Path;
        var boost = 0.0;
        int? pathDistance = null;

        if (path.StartsWith(contextPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path[(contextPath.Length + 1)..];
            var relativeDepth = relative.Count(c => c == '/');
            pathDistance = relativeDepth;
            boost = relativeDepth switch
            {
                0 => 2000,
                1 => 900,
                _ => 600,
            };
        }
        else
        {
            var contextParent = contextPath.LastIndexOf('/');
            if (contextParent > 0)
            {
                var parent = contextPath[..contextParent];
                if (path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
                    boost = 1000;
            }
        }

        if (boost == 0)
        {
            var contextSegments = contextPath.Split('/');
            var pathSegments = path.Split('/');
            var shared = 0;
            for (var i = 0; i < Math.Min(contextSegments.Length, pathSegments.Length); i++)
            {
                if (contextSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                    shared++;
                else break;
            }
            if (shared >= 2) boost = 500;
        }

        var segmentCount = path.Count(c => c == '/') + 1;
        boost -= segmentCount * 50;

        if (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(row.Name))
        {
            if (row.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) boost += 1000;
            else if (row.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) boost += 500;
        }

        return row with { Score = row.Score + boost, PathDistance = pathDistance ?? row.PathDistance };
    }

    /// <summary>
    /// <para>Dispatcher for the <see cref="IMeshQueryCore"/> surface. Routes
    /// based on <see cref="MeshQueryRequest.UserId"/>:</para>
    /// <list type="bullet">
    ///   <item><see cref="WellKnownUsers.System"/> (or null/empty) →
    ///     <b>unsecured</b> fan-out: providers that implement
    ///     <see cref="IMeshQueryCore"/> are invoked through that surface,
    ///     skipping per-result validators. Used by infrastructure callers
    ///     that must dodge the SecurityService → AccessAssignment query →
    ///     SecurityService recursion (SyncedQueryMeshNodes for
    ///     <c>_Access</c> walks, NodeType compile activities, framework
    ///     seeds).</item>
    ///   <item>Real user → <b>secured</b> fan-out: providers are invoked
    ///     through the public <see cref="IMeshQueryProvider.Query"/>
    ///     surface where <see cref="StorageAdapterMeshQueryProvider"/>
    ///     applies the per-result RLS validator chain for that user.
    ///     Per-user <c>workspace.GetQuery</c> calls route here so each
    ///     user sees only the nodes they have Read on — preventing cross-
    ///     user leakage through a shared cache.</item>
    /// </list>
    /// <para>The dispatch happens at the <c>IMeshQueryCore</c> seam so
    /// downstream consumers (<c>SyncedQueryMeshNodes</c> et al.) don't have
    /// to know about the secured surface. Stamp <c>request.UserId</c> at
    /// the call site and the right surface lights up.</para>
    /// </summary>
    IObservable<QueryResultChange<T>> IMeshQueryCore.Query<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options)
    {
        var isSystem = string.IsNullOrEmpty(request.UserId)
            || string.Equals(request.UserId, WellKnownUsers.System, StringComparison.Ordinal);
        var matched = SelectMatchingProviders(NamespacesForRequest(request));
        // 🚨 On the unsecured path, STAMP UserId=System EXPLICITLY. Some providers
        // (PostgreSqlMeshQuery.GetEffectiveUserId) bypass access control ONLY for an
        // explicit System UserId — an EMPTY UserId falls back to the ambient
        // AccessService.Context and silently applies the CALLER'S RLS. Infrastructure
        // reads (SecurityService access-element loads, SyncedQueryMeshNodes, the invitation
        // watcher, onboarding gate) pass an empty UserId expecting no-AC; without this stamp
        // a platform admin's own permission check under-loads the Admin-partition grants and
        // the Admin partition's nodes (invitations, grants) become invisible to query/search.
        // See Doc/Architecture/AccessControl.md → "The Admin partition".
        var coreRequest = isSystem && !string.Equals(request.UserId, WellKnownUsers.System, StringComparison.Ordinal)
            ? request with { UserId = WellKnownUsers.System }
            : request;
        // Subscribe on the DRAINABLE Query pool (see Query<T>): tracked so teardown DrainAll
        // cancel+joins the subscribe before the scope disposes.
        return QueryPool.SubscribeThroughPool(
            MergeProviderObservables(
                matched.Select(p => (isSystem
                    ? (p is IMeshQueryCore core
                        ? core.Query<T>(coreRequest, options)
                        : p.Query<T>(coreRequest, options))
                    // Real user: ALWAYS hit the secured provider surface
                    // (validators apply per-result RLS for request.UserId).
                    : p.Query<T>(request, options), p.Name)).ToList(),
                request));
    }

    /// <summary>
    /// Centralised provider gating — every fan-out in this class
    /// (<see cref="Query{T}(MeshQueryRequest)"/>, the
    /// <see cref="IMeshQueryCore"/> surface, <see cref="Autocomplete"/>,
    /// <see cref="Select{T}"/>) MUST go through this so a
    /// scoped query only subscribes / awaits providers that actually own
    /// (or claim) the partition. For a single-node-by-path lookup this
    /// typically resolves to ONE provider; the merge then waits on exactly
    /// that provider's Initial frame, so a stalled or irrelevant provider
    /// can't hold the merge hostage. Unscoped queries (no <c>namespace:</c>
    /// condition and no <c>path:</c> filter) fan to every provider — the
    /// <see cref="IMeshQueryProvider.Matches"/> contract documents this.
    /// </summary>
    /// <remarks>
    /// Always fans out to every provider. Each provider self-filters by its
    /// owned-namespaces / partition cache and returns empty for queries
    /// outside its scope. The legacy <c>IMeshQueryProvider.Matches</c>
    /// predicate was a centralised pre-filter; removing it lets each
    /// provider own the "is this mine?" decision in one place.
    /// </remarks>
    private IReadOnlyList<IMeshQueryProvider> SelectMatchingProviders(IReadOnlyList<string> _)
        => providers;

    /// <summary>
    /// Computes the namespace candidates for a <see cref="MeshQueryRequest"/>
    /// using its first effective query. Multi-query unions still subscribe
    /// every provider that matches ANY query's namespaces because the
    /// providers themselves handle each query independently — the catalog /
    /// per-instance fan-outs use a single query.
    /// </summary>
    private static IReadOnlyList<string> NamespacesForRequest(MeshQueryRequest request)
    {
        var firstQuery = request.EffectiveQueries.FirstOrDefault();
        if (string.IsNullOrEmpty(firstQuery))
            return Array.Empty<string>();
        var parsed = new QueryParser().Parse(firstQuery);
        return MergeQueryNamespaces(parsed);
    }

    /// <summary>
    /// Computes the namespace candidates for an autocomplete-style call
    /// (basePath + prefix). The aggregator extracts the first segment of
    /// basePath as the partition candidate; an empty basePath is unscoped
    /// (every provider participates).
    /// </summary>
    private static IReadOnlyList<string> NamespacesForBasePath(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return Array.Empty<string>();
        var firstSegment = basePath.TrimStart('/').Split('/', 2)[0];
        return string.IsNullOrEmpty(firstSegment)
            ? Array.Empty<string>()
            : new[] { firstSegment };
    }

    private IObservable<QueryResultChange<T>> MergeProviderObservables<T>(
        List<(IObservable<QueryResultChange<T>> Stream, string Provider)> observables,
        MeshQueryRequest request)
    {
        // Collect Initial from all providers, merge into a single Initial emission,
        // then forward subsequent (non-Initial) changes from ongoing providers.
        if (observables.Count == 0)
            return Observable.Empty<QueryResultChange<T>>();

        if (observables.Count == 1)
        {
            // Single provider — still funnel Initial through ClipMergedInitial
            // so request.Skip / request.Limit are applied. The provider itself
            // yields up to (Skip + Limit) items as a load cap (see
            // StorageAdapterMeshQueryProvider.QueryAsync) and defers the
            // final paging to the merge layer; bypassing ClipMergedInitial
            // here returned all (Skip + Limit) items instead of the trimmed
            // Limit window (repro:
            // HierarchicalBrowsingTests.QueryAsync_Generic_WithPaging_ReturnsPagedResults
            // — 10 items created, Skip=3 Limit=3, got 6 = Skip+Limit instead
            // of the expected 3).
            //
            // Pair items with their score (or 0 when the provider didn't
            // score this batch — see QueryResultChange.Scores contract).
            // Single-provider ordering is preserved when OrderBy is absent
            // and all scores are equal, because LINQ's OrderByDescending
            // is stable.
            return observables[0].Stream.Select(change =>
            {
                if (change.ChangeType != QueryChangeType.Initial)
                    return change;
                var hits = new List<(T Item, double Score)>(change.Items.Count);
                var scores = change.Scores;
                for (var j = 0; j < change.Items.Count; j++)
                {
                    var score = scores is not null && j < scores.Count ? scores[j] : 0.0;
                    hits.Add((change.Items[j], score));
                }
                return ClipMergedInitial<T>(hits, change, change.Query!, request);
            });
        }

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            // ────────────────────────────────────────────────────────────────
            // Initial-emission aggregation
            // ────────────────────────────────────────────────────────────────
            // Each provider emits one Initial carrying its slice of the
            // result. We:
            //   1. Wait for every provider's Initial (gate on
            //      initialCount == initialTarget).
            //   2. Concatenate every (item, score) pair into one flat list,
            //      deduping by Path (or reference identity for non-MeshNode T).
            //   3. Hand the flat list to ClipMergedInitial, which sorts by
            //      OrderBy first then Score desc (see that method), then
            //      applies Skip/Limit/select.
            //
            // No provider-priority hack. The old logic ordered
            // "writable persistence first, static catalog last" to keep static
            // entries from crowding out user content under a Limit clause.
            // That heuristic is replaced by the explicit Score sort: providers
            // that mean to win the top spot (PG with name-prefix hit,
            // path-proximity boost, vector-similarity) set a high score; the
            // static catalog typically sets 0 and naturally lands below
            // user content.
            //
            // The (item, score) pairing is preserved across the merge by
            // collecting into a list of tuples; converting back to parallel
            // arrays only at the ClipMergedInitial boundary.
            var providerHits = new List<(T Item, double Score)>[observables.Count];
            for (var k = 0; k < providerHits.Length; k++) providerHits[k] = new();
            var initialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var initialIdentities = new HashSet<T>();
            var initialCount = 0;
            var initialTarget = observables.Count;
            // Per-provider Initial tracking for the completion guard below.
            var initialSeen = new bool[observables.Count];
            ParsedQuery? lastQuery = null;
            var gate = new object();

            // Live-stream dedup: track Path so a Removed for a path
            // we never Added is dropped, and an Added for a path that's already
            // in the live set is dropped (same provider re-emitted, or
            // overlapping providers both saw the change).
            var liveItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var subscriptions = new List<IDisposable>();

            // Emits the merged Initial once the gate is satisfied. Must be called under `gate`.
            void EmitMergedInitialIfComplete(QueryResultChange<T> template)
            {
                if (initialCount != initialTarget)
                    return;
                foreach (var path in initialPaths)
                    liveItems.Add(path);
                // Flat concat across providers — no priority shuffle. ClipMergedInitial
                // performs the authoritative sort using OrderBy + Score, so the order we
                // feed it is irrelevant to the final shape.
                var ordered = new List<(T Item, double Score)>();
                for (var p = 0; p < providerHits.Length; p++)
                    ordered.AddRange(providerHits[p]);
                var parsed = lastQuery
                    ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? "");
                var clipped = ClipMergedInitial<T>(ordered, template, parsed, request);
                observer.OnNext(clipped);
            }

            for (var i = 0; i < observables.Count; i++)
            {
                var (obs, providerName) = observables[i];
                var idx = i;
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                // Pair items with their score (or 0 when the
                                // provider didn't score this batch). The
                                // contract: when Scores is non-null it MUST
                                // have the same length as Items.
                                var scores = change.Scores;
                                for (var j = 0; j < change.Items.Count; j++)
                                {
                                    var item = change.Items[j];
                                    var score = scores is not null && j < scores.Count
                                        ? scores[j]
                                        : 0.0;
                                    if (item is MeshNode node)
                                    {
                                        if (!string.IsNullOrEmpty(node.Path)
                                            && !initialPaths.Add(node.Path))
                                            continue;
                                        providerHits[idx].Add((item, score));
                                    }
                                    else if (initialIdentities.Add(item))
                                    {
                                        providerHits[idx].Add((item, score));
                                    }
                                }
                                lastQuery ??= change.Query;
                                if (!initialSeen[idx])
                                {
                                    initialSeen[idx] = true;
                                    initialCount++;
                                }

                                EmitMergedInitialIfComplete(change);
                            }
                        }
                        else
                        {
                            lock (gate)
                            {
                                if (TryFilterDuplicateLiveChange(change, liveItems, out var filtered))
                                    observer.OnNext(filtered);
                            }
                        }
                    },
                    ex => observer.OnError(ex),
                    // 🚨 Completion guard — the merged Initial gates on EVERY provider
                    // emitting one. A provider whose observable COMPLETES without an
                    // Initial (an Observable.Empty-shaped branch, a swallowed fault, an
                    // early-disposed inner chain) used to starve the gate FOREVER: the
                    // consumer's Take(1)/FirstAsync never fired and the caller hung until
                    // its transport timeout (prod: every real-user unpinned search on
                    // atioz hung 300s — DB idle, no error anywhere). The contract is
                    // documented at every provider ("returning Observable.Empty would
                    // hang the consumer") — ENFORCE it here: count the silent completion
                    // as an empty Initial so the merge proceeds, and log LOUDLY naming
                    // the provider so the offending code path gets fixed at its root.
                    () =>
                    {
                        lock (gate)
                        {
                            if (initialSeen[idx])
                                return;
                            Logger?.LogWarning(
                                "Query provider {Provider} completed WITHOUT emitting an Initial for query '{Query}' "
                                + "(user '{UserId}') — contract violation; counting as empty so the merged query can "
                                + "proceed. Fix the provider: every Query<T> observable must emit exactly one Initial.",
                                providerName, request.Query, request.UserId);
                            initialSeen[idx] = true;
                            initialCount++;
                            // Stamp Query even when EVERY provider went silent (lastQuery never
                            // set) — downstream consumers rely on QueryResultChange.Query being
                            // populated on an Initial; fall back to parsing the request.
                            var template = new QueryResultChange<T>
                            {
                                ChangeType = QueryChangeType.Initial,
                                Items = Array.Empty<T>(),
                                Timestamp = DateTimeOffset.UtcNow,
                                Query = lastQuery
                                    ?? new QueryParser().Parse(request.EffectiveQueries.FirstOrDefault() ?? ""),
                            };
                            EmitMergedInitialIfComplete(template);
                        }
                    });

                subscriptions.Add(sub);
            }

            return new System.Reactive.Disposables.CompositeDisposable(subscriptions);
        });
    }

    /// <summary>
    /// Sort + skip + clip the merged initial set. The authoritative ordering
    /// pass for every multi-provider Initial emission. Mirrors the post-collect
    /// pipeline that <c>StorageAdapterMeshQueryProvider.RunQueryNodes</c> runs per-provider.
    /// Also applies <c>select:</c> projection: static-node providers don't
    /// project to dictionaries on their own, so merging engine projections with
    /// raw static MeshNodes left mixed-shape results for callers.
    ///
    /// <para><b>Sort order — the canonical contract.</b></para>
    /// <list type="number">
    ///   <item><see cref="ParsedQuery.OrderBy"/> when the query author
    ///     specified <c>sort:Foo-desc</c> (or any other property). The
    ///     <see cref="QueryEvaluator.OrderResults"/> primitive is used so the
    ///     ordering rules match per-provider behavior (LastModified handles
    ///     DateTime, Name is case-insensitive, …). This is the FIRST sort
    ///     dimension — explicit user intent always wins.</item>
    ///   <item><b>Score descending</b> within ties (or as the sole sort key
    ///     when no <c>sort:</c> was specified). Each provider attaches a
    ///     numeric score per item via <see cref="QueryResultChange{T}.Scores"/>;
    ///     <see cref="MergeProviderObservables{T}"/> pairs items with their
    ///     scores and hands them here. Higher score = stronger match.
    ///     See <see cref="QueryResultChange{T}.Scores"/> for the per-provider
    ///     scoring conventions.</item>
    ///   <item>Insertion order as the final tiebreaker — preserves the
    ///     provider's own deterministic ordering for two items at the same
    ///     score (e.g. two PG rows tied on the prefix bonus).</item>
    /// </list>
    ///
    /// <para><b>Why score sort lives here, not in each provider.</b> A single
    /// provider can rank within itself, but the AGGREGATOR is where
    /// cross-provider tie-breaking matters: a PG hit with name-prefix score
    /// 100 must beat a static-catalog hit with score 0 for the same query.
    /// Putting the score sort in <c>ClipMergedInitial</c> ensures every
    /// downstream consumer of <see cref="Query{T}"/> sees a single deterministic top-N regardless
    /// of which providers contributed.</para>
    /// </summary>
    private static QueryResultChange<T> ClipMergedInitial<T>(
        List<(T Item, double Score)> hits,
        QueryResultChange<T> change,
        ParsedQuery parsed,
        MeshQueryRequest request)
    {
        IEnumerable<(T Item, double Score)> merged = hits;
        if (parsed.OrderBy is { } orderBy)
        {
            // OrderBy is the FIRST sort dimension when present — user intent
            // beats provider scoring. Strip to items, sort, re-pair with
            // scores (preserved by item identity). For non-MeshNode items
            // the OrderBy is a no-op (QueryEvaluator only handles MeshNode);
            // skip the sort to avoid mangling the score order.
            if (typeof(T) == typeof(MeshNode) || hits.Any(h => h.Item is MeshNode))
            {
                var evaluator = new QueryEvaluator();
                var scoreByItem = new Dictionary<MeshNode, double>();
                foreach (var (item, score) in hits)
                {
                    if (item is MeshNode node && node.Path is not null)
                        scoreByItem[node] = score;
                }
                var ordered = evaluator
                    .OrderResults(hits.Select(h => h.Item).OfType<MeshNode>(), orderBy)
                    .ToList();
                merged = ordered.Select(node => ((T)(object)node,
                    scoreByItem.TryGetValue(node, out var s) ? s : 0.0));
            }
        }
        else
        {
            // No explicit OrderBy → score IS the sort dimension. Sort
            // descending so the highest-relevance match lands first;
            // insertion order is the implicit tiebreaker because
            // OrderByDescending is stable in LINQ-to-objects.
            merged = hits.OrderByDescending(h => h.Score);
        }
        if (request.Skip is int skip && skip > 0)
            merged = merged.Skip(skip);
        var effectiveLimit = request.Limit ?? parsed.Limit;
        if (effectiveLimit is int limit && limit > 0)
            merged = merged.Take(limit);
        var finalList = merged.ToList();
        var items = new List<T>(finalList.Count);
        var scores = new List<double>(finalList.Count);
        foreach (var (item, score) in finalList)
        {
            if (parsed.Select is { } select && item is MeshNode node)
            {
                items.Add((T)(object)ParsedQuery.ProjectToSelect(node, select));
            }
            else
            {
                items.Add(item);
            }
            scores.Add(score);
        }
        return change with { Items = items, Scores = scores };
    }

    /// <summary>
    /// Strips items from a non-Initial change that are duplicates against the
    /// live dedup set (overlapping provider emitted the same change again, or
    /// a Removed arrived for a path we never Added). Returns false when the
    /// change has no usable items left, so the caller can drop the emission.
    /// </summary>
    private static bool TryFilterDuplicateLiveChange<T>(
        QueryResultChange<T> change,
        HashSet<string> liveItems,
        out QueryResultChange<T> filtered)
    {
        var kept = new List<T>(change.Items.Count);
        foreach (var item in change.Items)
        {
            if (item is not MeshNode node || string.IsNullOrEmpty(node.Path))
            {
                kept.Add(item);
                continue;
            }
            switch (change.ChangeType)
            {
                case QueryChangeType.Added or QueryChangeType.Updated:
                    if (liveItems.Add(node.Path))
                        kept.Add(item);
                    else if (change.ChangeType == QueryChangeType.Updated)
                        kept.Add(item); // updates flow through even if path already known
                    break;
                case QueryChangeType.Removed:
                    if (liveItems.Remove(node.Path))
                        kept.Add(item);
                    break;
                default:
                    kept.Add(item);
                    break;
            }
        }
        if (kept.Count == 0)
        {
            filtered = change;
            return false;
        }
        filtered = change with { Items = kept };
        return true;
    }

    /// <summary>
    /// Reads a single property value of the node at <paramref name="path"/>,
    /// returning the first non-null match across the provider fan-out (or the
    /// default value when no provider resolves the node).
    /// </summary>
    /// <typeparam name="T">The expected type of the property value.</typeparam>
    /// <param name="path">The full mesh path of the node to read.</param>
    /// <param name="property">The name of the property to project from the node.</param>
    /// <returns>An observable emitting the property value, or default when not found.</returns>
    public IObservable<T?> Select<T>(string path, string property)
    {
        var matched = SelectMatchingProviders(NamespacesForBasePath(path));
        // Merge each provider's single-emission Select observable, take the first
        // non-null. Stays reactive end-to-end — no Task bridge, no ToTask.
        return matched
            .Select(p => p.Select<T>(path, property, Options))
            .Merge()
            .Where(r => r is not null)
            .FirstOrDefaultAsync();
    }

}
