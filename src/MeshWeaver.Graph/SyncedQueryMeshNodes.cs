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
        collectionName)
    {
    }

    private SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        IReadOnlyList<string> queries,
        BehaviorSubject<ImmutableHashSet<string>> pathSet,
        string? collectionName
    ) : base(workspace, dataSourceId,
            ws => BuildReadStream(ws, queries, pathSet),
            collectionName)
    {
        Queries = queries;
        _pathSet = pathSet;
    }

    private readonly BehaviorSubject<ImmutableHashSet<string>> _pathSet;

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
    /// Single-subscription pipeline: per-query mesh-query → fold every
    /// Initial / Added / Updated / Removed event into a path → MeshNode
    /// dictionary; emit the values whenever it changes.
    ///
    /// <para>
    /// We accumulate values directly from <see cref="IMeshQueryProvider.ObserveQuery"/>
    /// rather than opening per-path remote streams + <c>CombineLatest</c>:
    /// the upstream query already carries the latest <see cref="MeshNode"/>
    /// for every result-set member through its <c>Updated</c> events, and
    /// the per-path-remote-stream + <c>CombineLatest</c> design produces
    /// the well-known feedback-loop pattern (outer re-emits when inner
    /// values change → <c>CombineLatest</c> tears down + rebuilds → cycle).
    /// </para>
    ///
    /// <para>
    /// For multi-query unions, every query updates the same dictionary —
    /// last-write-wins on duplicate paths, which is fine because all
    /// queries return the same MeshNode for a given path.
    /// </para>
    /// </summary>
    private static IObservable<IEnumerable<MeshNode>> BuildReadStream(
        IWorkspace workspace,
        IReadOnlyList<string> queries,
        BehaviorSubject<ImmutableHashSet<string>> pathSet)
    {
        // Iterate over IEnumerable<IMeshQueryProvider> — GetRequiredService<>
        // returns only the last-registered provider (typically
        // StaticNodeQueryProvider, which completes after Initial), so runtime
        // CreateNode / DeleteNode events never reach the synced collection.
        // Unioning every provider gives us static seeds AND the
        // changeNotifier-driven InMemoryMeshQuery / FileSystemMeshQuery /
        // PostgreSqlMeshQuery surfaces required for runtime mutation
        // propagation.
        //
        // UserId = WellKnownUsers.System bypasses the RLS read-validator chain
        // so the synced query stays infrastructure-level. Without this, a
        // SecurityService-driven synced query for `nodeType:AccessAssignment`
        // would recurse: the per-node read validator calls SecurityService
        // which subscribes back to this same stream → deadlock waiting for
        // its own Initial emission.
        var providers = workspace.Hub.ServiceProvider
            .GetServices<IMeshQueryProvider>()
            .ToList();
        var options = workspace.Hub.JsonSerializerOptions;

        IObservable<QueryResultChange<MeshNode>> ObserveOne(string query) =>
            providers
                .Select(p => p.ObserveQuery<MeshNode>(
                    MeshQueryRequest.FromQuery(query, WellKnownUsers.System),
                    options))
                .Merge();

        var changes = queries
            .Select(ObserveOne)
            .Merge();

        // Fold change deltas into a path → MeshNode dictionary.
        return changes
            .Scan(
                ImmutableDictionary<string, MeshNode>.Empty,
                (dict, change) => change.ChangeType switch
                {
                    // Initial / Reset replace the snapshot for THIS query —
                    // but in a multi-query union we'd lose other queries'
                    // entries. Treat every event as additive/removal so the
                    // unioned dict accumulates correctly.
                    QueryChangeType.Initial or QueryChangeType.Reset
                        or QueryChangeType.Added or QueryChangeType.Updated =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.SetItem(n.Path, n)),
                    QueryChangeType.Removed =>
                        change.Items.Aggregate(dict, (d, n) =>
                            string.IsNullOrEmpty(n.Path) ? d : d.Remove(n.Path)),
                    _ => dict,
                })
            .Do(dict => pathSet.OnNext(dict.Keys.ToImmutableHashSet()))
            .DistinctUntilChanged()
            .Select(dict => (IEnumerable<MeshNode>)dict.Values);
    }
}
