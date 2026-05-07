using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// <see cref="VirtualTypeSource{T}"/> specialised for "live mesh-query mirror
/// of <see cref="MeshNode"/>s". Accepts <em>one or more</em> mesh-query
/// strings and exposes the <strong>union</strong> of their result sets as a
/// single live <see cref="MeshNode"/> collection.
///
/// <para>The read pipeline (single subscription, shared by every consumer):</para>
/// <list type="number">
///   <item>For each query in <see cref="Queries"/>:
///     <see cref="IMeshQueryProvider.ObserveQuery{MeshNode}"/> →
///     <c>Scan</c> the Initial / Added / Updated / Removed deltas into a
///     running set of paths.</item>
///   <item>Combine all per-query path sets via <c>CombineLatest</c> and union
///     them, then <c>DistinctUntilChanged</c> with element equality on the
///     unioned set so identical sets don't trigger redundant
///     re-subscription to per-path streams.</item>
///   <item>For every path in the current set, resolve the workspace's per-node
///     remote stream (<c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>),
///     then <c>CombineLatest</c> them into the synced collection.
///     <c>Switch</c> when the path set changes — old per-path subscriptions
///     are dropped, new ones added.</item>
/// </list>
///
/// <para>Writes — via the reducer registered in
/// <see cref="SyncedQueryDataSourceExtensions.AddSyncedQuery"/>:
/// <c>workspace.GetStream(new MeshNodeReference(path))</c> resolves to the
/// cached per-path remote stream. <c>.Update(...)</c> on it propagates through
/// the synchronization protocol to the owning per-node hub for persistence.</para>
///
/// <para>Discriminator (multiple synced sources on the same hub): each source's
/// reducer first checks <see cref="Owns"/> against its live unioned path set;
/// the first reducer that returns non-null wins.</para>
/// </summary>
public sealed record SyncedQueryMeshNodes : VirtualTypeSource<MeshNode>
{
    /// <summary>
    /// Public constructor for a single-query synced collection — defers to
    /// the multi-query constructor with a one-element array.
    /// </summary>
    public SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        string query,
        string? collectionName = null
    ) : this(workspace, dataSourceId, new[] { query }, collectionName)
    {
    }

    /// <summary>
    /// Public constructor for a multi-query synced collection. The result
    /// is the <em>union</em> of every <paramref name="queries"/> entry's
    /// matched paths. Each query opens an independent
    /// <see cref="IMeshQueryProvider.ObserveQuery"/> subscription; per-path
    /// remote streams are de-duplicated by path before
    /// <c>CombineLatest</c>.
    /// </summary>
    public SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        string? collectionName = null
    ) : this(workspace, dataSourceId, queries,
        new BehaviorSubject<ImmutableHashSet<string>>(ImmutableHashSet<string>.Empty),
        new Subject<QueryResultChange<MeshNode>>(),
        collectionName)
    {
    }

    private SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        Subject<QueryResultChange<MeshNode>> externalChanges,
        string? collectionName
    ) : base(workspace, dataSourceId,
            ws => BuildReadStream(ws, queries, pathSet, externalChanges),
            collectionName)
    {
        Queries = queries;
        _pathSet = pathSet;
        _externalChanges = externalChanges;
    }

    private readonly BehaviorSubject<ImmutableHashSet<string>> _pathSet;

    // Synchronous side-channel for synthetic Removed events pushed by the
    // delete handler (see <see cref="NotifyDeleted"/>). Merged into the per-query
    // upstream stream in <see cref="BuildReadStream"/> so the downstream Scan +
    // _pathSet update treats them identically to upstream-driven Removed events.
    private readonly Subject<QueryResultChange<MeshNode>> _externalChanges;

    /// <summary>The one-or-more mesh-query strings whose results union into this collection.</summary>
    public IReadOnlyList<string> Queries { get; }

    /// <summary>
    /// Live snapshot of the unioned set of paths currently matched by
    /// <see cref="Queries"/>. Updated as the upstream mesh-query streams
    /// emit Initial / Added / Removed deltas. Read synchronously by the
    /// reducer to decide whether this source owns a given path.
    /// </summary>
    public ImmutableHashSet<string> CurrentPaths => _pathSet.Value;

    /// <summary>True when <paramref name="path"/> is in this source's live unioned result set.</summary>
    public bool Owns(string path) => _pathSet.Value.Contains(path);

    /// <summary>
    /// Pushes a synthetic <see cref="QueryChangeType.Removed"/> event into this
    /// source's pipeline. Callers should typically gate this on
    /// <see cref="Owns"/> first to avoid emitting spurious removals for paths
    /// that were never in the result set.
    ///
    /// <para>Used by the framework's delete handler as a synchronous reliability
    /// path on top of the upstream change-notifier-driven Removed event:
    /// the upstream path can be debounced, security-filtered, or stalled by
    /// per-hub locks, which causes flaky in-memory delete propagation. A direct
    /// notify here removes the path from the dictionary immediately, the
    /// downstream <c>DistinctUntilChanged</c> de-duplicates if the upstream
    /// later emits the same Removed event.</para>
    /// </summary>
    public void NotifyDeleted(string path, MeshNode? node = null)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var item = node ?? new MeshNode(path);
        _externalChanges.OnNext(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Removed,
            Items = new[] { item },
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Per <see href="../Doc/Architecture/CqrsAndContentAccess.md">CQRS</see>:
    /// queries are for finding sets of paths, NOT for reading content.
    /// The pipeline is:
    ///
    /// <list type="number">
    ///   <item>For each query, <see cref="IMeshQueryProvider.ObserveQuery"/>
    ///         emits the set of matching paths (we IGNORE the MeshNode
    ///         payload it carries — content read here would be lagged
    ///         and stale).</item>
    ///   <item>Union the per-query path sets via <c>CombineLatest</c> +
    ///         <c>DistinctUntilChanged</c> so the downstream stream only
    ///         re-emits when paths add or remove.</item>
    ///   <item>For each path in the unioned set, open ONE
    ///         <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>
    ///         — the authoritative live content for that node from its
    ///         owning hub. Combine the per-path streams via
    ///         <c>Switch</c> + <c>CombineLatest</c> so an Update inside a
    ///         single node propagates without re-running the path-set
    ///         query.</item>
    /// </list>
    ///
    /// <para>The previous design accumulated content directly from the
    /// query's Updated events. That violated the CQRS contract — the
    /// query layer is eventually consistent and Update events lagged
    /// real writes by 10–100 ms. The remote-stream-per-path design
    /// reads from the OWNING hub's workspace so every snapshot reflects
    /// authoritative current state.</para>
    /// </summary>
    private static IObservable<IEnumerable<MeshNode>> BuildReadStream(
        IWorkspace workspace,
        IReadOnlyList<string> queries,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        Subject<QueryResultChange<MeshNode>> externalChanges)
    {
        // Defer everything to subscribe-time: any DI resolution failure
        // (missing IMeshQueryCore registration on this hub, etc.) becomes an
        // OnError on the returned observable instead of a synchronous throw
        // out of StreamProvider — the caller's subscription error handler
        // (e.g. AccessControlPipeline's permission-check try/catch) sees it,
        // never the StreamUpdates() / GetQuery() call site that triggered
        // the build.
        return Observable.Defer(() => BuildReadStreamCore(workspace, queries, pathSet, externalChanges));
    }

    private static IObservable<IEnumerable<MeshNode>> BuildReadStreamCore(
        IWorkspace workspace,
        IReadOnlyList<string> queries,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        Subject<QueryResultChange<MeshNode>> externalChanges)
    {
        // 🚨 Iterate the dedicated ISyncedMeshNodeQueryProvider marker
        // interface — NOT IMeshQueryProvider. The marker pulls in the
        // unsecured providers that are safe to consume from a synced
        // query (StaticNodeQueryProvider + the unsecured persistence
        // surface) and excludes the secured InMemoryMeshQuery whose
        // SecurityService dependency creates a re-entrancy cycle when
        // the synced query is the one feeding SecurityService itself
        // (`nodeType:AccessAssignment`). Type-tagged registration —
        // not a name-string filter — see ISyncedMeshNodeQueryProvider.
        //
        // The previous design used IMeshQueryCore (a single in-memory
        // provider) and missed every static-node-provider entry: chat
        // dropdowns and every synced-query consumer were silently empty
        // even though `IMeshService.QueryAsync` (which fans out across
        // all IMeshQueryProviders) returned the same nodes fine. Marker
        // fan-out fixes that without the cycle.
        var providers = workspace.Hub.ServiceProvider
            .GetServices<ISyncedMeshNodeQueryProvider>()
            .ToArray();
        var options = workspace.Hub.JsonSerializerOptions;

        IObservable<QueryResultChange<MeshNode>> ObserveOne(string query)
        {
            var request = MeshQueryRequest.FromQuery(query, WellKnownUsers.System);
            if (providers.Length == 0)
                return Observable.Empty<QueryResultChange<MeshNode>>();
            return providers
                .Select(p => p.ObserveQuery<MeshNode>(request, options))
                .Merge();
        }

        // Hub-level change-feed deletion fast-path: when ANY hub publishes a
        // delete via IMeshChangeFeed (the canonical post-delete dispatch in
        // <c>HandleDeleteNodeRequest</c>), translate it into a synthetic
        // Removed event for the path-set Scan. Synchronous reliability path
        // on top of the upstream IMeshQueryProvider.ObserveQuery's Removed
        // event, which can be debounced/stalled by the persistence layer's
        // change-notifier and security-filter chain.
        var changeFeed = workspace.Hub.ServiceProvider.GetService<IMeshChangeFeed>();
        var feedRemovals = changeFeed is null
            ? Observable.Empty<QueryResultChange<MeshNode>>()
            : Observable.Create<QueryResultChange<MeshNode>>(observer =>
                changeFeed.Subscribe(
                    e => observer.OnNext(new QueryResultChange<MeshNode>
                    {
                        ChangeType = QueryChangeType.Removed,
                        Items = new[] { new MeshNode(e.Path) },
                        Timestamp = e.Timestamp,
                    }),
                    MeshChangeKind.Deleted));

        // Per-query Initial gating: with multi-query unions AND multi-provider
        // fan-out, the gate must wait for EVERY provider to send Initial for
        // EVERY query — not just the first provider per query. Otherwise an
        // empty Initial from a fast provider (e.g. StaticNodeQueryProvider
        // returning [] for a `nodeType:Code` query because no Code nodes are
        // in its static set) would race ahead of the slower provider
        // (InMemoryMeshQueryCore returning the persisted Code files) and the
        // first .Take(1) consumer (MeshNodeCompilationService.discoverCodeFiles)
        // would see "no source files" — exactly the regression that broke the
        // Hosting.Monolith.Test compile suite.
        var providerCount = providers.Length;
        var taggedChanges = queries
            .Select((q, i) => ObserveOne(q).Select(c => (Change: c, QueryIndex: i, IsExternal: false)))
            .Merge()
            .Merge(externalChanges.Select(c => (Change: c, QueryIndex: -1, IsExternal: true)))
            .Merge(feedRemovals.Select(c => (Change: c, QueryIndex: -1, IsExternal: true)));

        // Fold change deltas into a path → MeshNode dictionary AND a per-query
        // count of how many provider Initials have been received. Suppress
        // downstream emissions until count[i] == providerCount for every
        // query — that turns the first .Take(1) consumer into "first complete
        // snapshot" instead of "first partial snapshot".
        return taggedChanges
            .Scan(
                (Dict: ImmutableDictionary<string, MeshNode>.Empty,
                 InitialCounts: ImmutableArray.CreateRange(Enumerable.Repeat(0, queries.Count))),
                (state, tagged) =>
                {
                    var (dict, counts) = state;
                    var change = tagged.Change;

                    // Track Initial / Reset arrivals by query index — increment
                    // the per-query count so the gate waits for ALL providers.
                    if (!tagged.IsExternal &&
                        (change.ChangeType == QueryChangeType.Initial ||
                         change.ChangeType == QueryChangeType.Reset) &&
                        tagged.QueryIndex >= 0 &&
                        tagged.QueryIndex < counts.Length)
                    {
                        counts = counts.SetItem(
                            tagged.QueryIndex,
                            counts[tagged.QueryIndex] + 1);
                    }

                    var nextDict = change.ChangeType switch
                    {
                        QueryChangeType.Initial or QueryChangeType.Reset
                            or QueryChangeType.Added or QueryChangeType.Updated =>
                            change.Items.Aggregate(dict, (d, n) =>
                                string.IsNullOrEmpty(n.Path) ? d : d.SetItem(n.Path, n)),
                        QueryChangeType.Removed =>
                            change.Items.Aggregate(dict, (d, n) =>
                                string.IsNullOrEmpty(n.Path) ? d : d.Remove(n.Path)),
                        _ => dict,
                    };

                    return (nextDict, counts);
                })
            .Where(state => providerCount == 0 ||
                            state.InitialCounts.All(c => c >= providerCount))
            .Do(state => pathSet.OnNext(state.Dict.Keys.ToImmutableHashSet()))
            .Select(state => state.Dict)
            .DistinctUntilChanged()
            .Select(dict => (IEnumerable<MeshNode>)dict.Values);
    }
}
