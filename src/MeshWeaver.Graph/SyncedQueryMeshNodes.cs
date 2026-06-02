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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// <see cref="VirtualTypeSource{T}"/> specialised for "live mesh-query mirror
/// of <see cref="MeshNode"/>s". Accepts <em>one or more</em> mesh-query
/// strings and exposes the <strong>union</strong> of their result sets as a
/// single live <see cref="MeshNode"/> collection.
///
/// <para>The read pipeline (single subscription, shared by every consumer):</para>
/// <list type="number">
///   <item>For each query in <see cref="Queries"/>: subscribe to
///     <see cref="IMeshQueryCore.Query{MeshNode}"/> with
///     <see cref="WellKnownUsers.System"/> identity (so the security pipeline
///     short-circuits and there's no recursion back through SecurityService).</item>
///   <item>A single <c>Scan</c> folds every query's
///     <c>Initial</c>/<c>Reset</c>/<c>Added</c>/<c>Updated</c>/<c>Removed</c>
///     deltas into an <see cref="ImmutableDictionary{TKey,TValue}"/> keyed by
///     <see cref="MeshNode.Path"/> — the dictionary IS the live collection.
///     A side <see cref="BehaviorSubject{T}"/> keeps the live path set in
///     parallel for synchronous <see cref="Owns"/> checks by the write reducer.</item>
///   <item>Per-query Initial gating: the downstream <c>Where(...)</c>
///     suppresses emissions until every query has produced its first
///     <c>Initial</c>/<c>Reset</c>, so the first <c>.Take(1)</c> consumer
///     sees a complete snapshot rather than a partial one. After the gate
///     opens, each change re-emits the full <c>dict.Values</c>.</item>
/// </list>
///
/// <para>Reads come from the MeshNode payloads carried by the query events;
/// the upstream <see cref="IMeshQueryCore.Query"/> pipeline already
/// mirrors per-hub change notifications into <c>Updated</c>/<c>Removed</c>
/// events, so the snapshot stays current without per-node read subscriptions.
/// Static-node providers (built-in agents, language models, embedded markdown)
/// fan into the same <c>IMeshQueryCore</c>, so the synced collection sees
/// them too.</para>
///
/// <para>Writes — via the reducer registered in
/// <see cref="SyncedQueryDataSourceExtensions.AddSyncedQuery"/>:
/// <c>workspace.GetStream(new MeshNodeReference(path))</c> resolves to the
/// workspace's cached per-(addr, ref) remote stream — opening a write-side
/// subscription on demand if no other caller has already cached one.
/// <c>.Update(...)</c> on that stream propagates through the synchronization
/// protocol to the owning per-node hub for persistence.</para>
///
/// <para>Discriminator (multiple synced sources on the same hub): each
/// source's write reducer first checks <see cref="Owns"/> against its live
/// path set; the first reducer that returns non-null wins.</para>
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
    /// <see cref="IMeshQueryProvider.Query"/> subscription; per-path
    /// remote streams are de-duplicated by path before
    /// <c>CombineLatest</c>.
    /// </summary>
    public SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        string? collectionName = null
    ) : this(workspace, dataSourceId, queries, WellKnownUsers.System, collectionName)
    {
    }

    /// <summary>
    /// Per-user constructor: opens the upstream <see cref="IMeshQueryCore"/>
    /// subscription under <paramref name="userIdentity"/> so the secured
    /// surface applies that user's RLS to the cached snapshot. Two users
    /// reading the same logical query through <c>workspace.GetQuery</c>
    /// get TWO independent instances (one per user). Pass
    /// <see cref="WellKnownUsers.System"/> for infrastructure callers
    /// (SecurityService's <c>_Access</c> walks, NodeType compile activities,
    /// post-deploy seeds) that intentionally need the validator-bypassing
    /// surface — never use it from a user-facing read seam.
    /// </summary>
    public SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        string userIdentity,
        string? collectionName = null
    ) : this(workspace, dataSourceId, queries, userIdentity,
        new BehaviorSubject<ImmutableHashSet<string>>(ImmutableHashSet<string>.Empty),
        new Subject<QueryResultChange<MeshNode>>(),
        collectionName)
    {
    }

    private SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        string userIdentity,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        Subject<QueryResultChange<MeshNode>> externalChanges,
        string? collectionName
    ) : base(workspace, dataSourceId,
            ws => BuildReadStream(ws, queries, userIdentity, pathSet, externalChanges),
            collectionName)
    {
        Queries = queries;
        UserIdentity = userIdentity;
        _pathSet = pathSet;
        _externalChanges = externalChanges;
    }

    /// <summary>User identity used to open the upstream query. Cache key
    /// component — synced queries cache per (id, userIdentity).</summary>
    public string UserIdentity { get; }

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
    /// Builds the live snapshot pipeline:
    ///
    /// <list type="number">
    ///   <item>For each query, subscribe to
    ///         <see cref="IMeshQueryCore.Query"/>; merge every
    ///         per-query stream plus the
    ///         <see cref="_externalChanges"/> side-channel (synthetic
    ///         <see cref="NotifyDeleted"/> events) and
    ///         <see cref="IMeshChangeFeed"/> deletion fast-path into a
    ///         single tagged stream of
    ///         <see cref="QueryResultChange{MeshNode}"/>.</item>
    ///   <item>A single <c>Scan</c> folds the deltas into
    ///         <see cref="ImmutableDictionary{TKey,TValue}"/> keyed by
    ///         <see cref="MeshNode.Path"/> AND a per-query Initial-count
    ///         array. <c>Initial</c>/<c>Reset</c>/<c>Added</c>/<c>Updated</c>
    ///         set/replace the dict entry; <c>Removed</c> drops it.</item>
    ///   <item>Suppress emissions until every query has produced its first
    ///         provider Initial, so the first <c>.Take(1)</c> consumer sees
    ///         the complete path set rather than a partial one. Push the
    ///         live path set into <see cref="_pathSet"/> for synchronous
    ///         <see cref="Owns"/> checks by the write reducer, then emit
    ///         <c>dict.Values</c>.</item>
    /// </list>
    ///
    /// <para>For single-node content reads on a known path use
    /// <see cref="MeshWeaver.Mesh.MeshNodeStreamExtensions.GetMeshNodeStream(MeshWeaver.Data.IWorkspace,string)"/>
    /// instead — the synced collection is for live <em>collections</em>
    /// (chat dropdowns, AccessAssignment scope-walks, NodeType source/test
    /// listings), not for fetching one node by path. See
    /// <see href="../Doc/Architecture/CqrsAndContentAccess.md">CQRS</see>.</para>
    /// </summary>
    private static IObservable<IEnumerable<MeshNode>> BuildReadStream(
        IWorkspace workspace,
        IReadOnlyList<string> queries,
        string userIdentity,
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
        return Observable.Defer(() => BuildReadStreamCore(workspace, queries, userIdentity, pathSet, externalChanges));
    }

    private static IObservable<IEnumerable<MeshNode>> BuildReadStreamCore(
        IWorkspace workspace,
        IReadOnlyList<string> queries,
        string userIdentity,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        Subject<QueryResultChange<MeshNode>> externalChanges)
    {
        // Single IMeshQueryCore — the unsecured query surface. Has no
        // SecurityService dependency, so SecurityService can consume a
        // synced query (`nodeType:AccessAssignment`) without an Autofac
        // cycle. Static-node providers are surfaced as a sibling
        // IMeshQueryProvider (StaticNodeQueryProvider) and merged at the
        // top-level MeshQuery, so a single Query returns persistence
        // + static union — no DI-marker fan-out, no per-provider gating.
        var queryCore = workspace.Hub.ServiceProvider.GetService<IMeshQueryCore>();
        var options = workspace.Hub.JsonSerializerOptions;
        var diagLogger = workspace.Hub.ServiceProvider
            .GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.SyncedQuery");

        diagLogger?.LogDebug(
            "[SyncedQuery] BuildReadStreamCore hub={HubAddress} queries=[{Queries}] core={CoreType}",
            workspace.Hub.Address,
            string.Join(" | ", queries),
            queryCore?.GetType().Name ?? "(null)");

        // 🚨 ONE call into the query layer — providers union every query's
        // hits by Path (see StorageAdapterMeshQueryProvider.CollectMatchedAsync;
        // PostgreSQL pushes UNION down to SQL via PostgreSqlMeshQuery.QueryAsync).
        // No per-query merge / per-query Initial gating in user code.
        IObservable<QueryResultChange<MeshNode>> upstream;
        if (queryCore == null)
        {
            diagLogger?.LogWarning(
                "[SyncedQuery] No IMeshQueryCore registered on hub={HubAddress} — queries=[{Queries}] returns Empty",
                workspace.Hub.Address, string.Join(" | ", queries));
            upstream = Observable.Empty<QueryResultChange<MeshNode>>();
        }
        else
        {
            // Per-user RLS: stamp the SyncedQuery's caller identity on the
            // MeshQueryRequest. The query providers' secured surface uses
            // this UserId to filter per-result via validators. System-loaded
            // synced queries (SecurityService's _Access walks, NodeType
            // compile activities, framework seeds) still pass System here
            // — the unsecured surface short-circuits the validator chain so
            // we don't recurse SecurityService → AccessAssignment query →
            // SecurityService.
            var request = MeshQueryRequest.FromQueries(queries, userIdentity);
            diagLogger?.LogDebug(
                "[SyncedQuery] Opening upstream queries=[{Queries}] under userIdentity={UserIdentity}",
                string.Join(" | ", queries), userIdentity);
            // Dispatch lives in MeshQuery's IMeshQueryCore.Query
            // (Hosting layer — see git history on MeshQuery.cs): when
            // request.UserId is System, it routes to the provider's
            // unsecured IMeshQueryCore surface (validator-bypass for the
            // SecurityService → AccessAssignment recursion); when it's a
            // real user, it routes to the provider's secured surface so
            // StorageAdapterMeshQueryProvider applies the per-result
            // validator chain for that user. Graph stays decoupled from
            // Hosting — we just pass userIdentity through and let the
            // dispatch happen at the consumer.
            upstream = queryCore.Query<MeshNode>(request, options).Do(change =>
                diagLogger?.LogDebug(
                    "[SyncedQuery] queries=[{Queries}] change={Type} count={Count}",
                    string.Join(" | ", queries), change.ChangeType, change.Items?.Count ?? 0));
        }

        // Hub-level change-feed deletion fast-path: when ANY hub publishes a
        // delete via IMeshChangeFeed (the canonical post-delete dispatch in
        // <c>HandleDeleteNodeRequest</c>), translate it into a synthetic
        // Removed event for the path-set Scan. Synchronous reliability path
        // on top of the upstream IMeshQueryProvider.Query's Removed
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

        var allChanges = upstream.Merge(externalChanges).Merge(feedRemovals);

        // Fold change deltas into a path → MeshNode dictionary. The engine
        // already unions across all queries, so a single Initial seeds the
        // complete snapshot; subsequent Added/Updated/Removed events apply
        // verbatim.
        return allChanges
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty,
                (dict, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset
                        or QueryChangeType.Added or QueryChangeType.Updated =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.SetItem(n.Path, n)),
                    QueryChangeType.Removed =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.Remove(n.Path)),
                    _ => dict,
                })
            .Do(dict => diagLogger?.LogDebug(
                "[SyncedQuery] snapshot hub={HubAddress} count={Count} keys=[{Keys}]",
                workspace.Hub.Address, dict.Count,
                string.Join(", ", dict.Keys.Take(10))))
            .Do(dict => pathSet.OnNext(dict.Keys.ToImmutableHashSet()))
            .DistinctUntilChanged()
            .Select(dict => (IEnumerable<MeshNode>)dict.Values);
    }
}
