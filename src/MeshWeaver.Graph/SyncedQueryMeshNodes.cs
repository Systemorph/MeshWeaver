using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// <see cref="VirtualTypeSource{T}"/> specialised for "live mesh-query mirror
/// of <see cref="MeshNode"/>s". The read side is built from per-node remote
/// streams (one per matched path); the write side is exposed via a reducer
/// for <see cref="MeshNodeReference"/> with a non-null <c>Path</c>, registered
/// alongside the data source.
///
/// <para>Reads:</para>
/// <list type="number">
///   <item>Observe <see cref="IMeshQueryProvider.ObserveQuery{MeshNode}"/> — derive
///     the live set of matched paths.</item>
///   <item>For every path, get the workspace's per-node remote stream
///     (<c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;(addr, ref)</c>).
///     The workspace caches that stream per <c>(address, reference)</c> with
///     <c>Replay(1).RefCount()</c> semantics — same instance is returned for
///     every reader, including the reducer's write path.</item>
///   <item><c>CombineLatest</c> them into the synced collection.</item>
/// </list>
///
/// <para>Writes — via the reducer registered in
/// <see cref="SyncedQueryDataSourceExtensions.AddSyncedQuery"/>:
/// <c>workspace.GetStream(new MeshNodeReference(path))</c> resolves to the
/// cached per-path remote stream. <c>.Update(...)</c> on it propagates through
/// the synchronization protocol to the owning per-node hub for persistence.</para>
///
/// <para>Discriminator (multiple synced sources on the same hub): each source's
/// reducer first checks <see cref="Owns"/> against its live path set; the
/// first reducer that returns non-null wins. This keeps writes routed to the
/// source whose query actually matches the path.</para>
/// </summary>
public sealed record SyncedQueryMeshNodes : VirtualTypeSource<MeshNode>
{
    private readonly BehaviorSubject<ImmutableHashSet<string>> _pathSet = new(ImmutableHashSet<string>.Empty);

    public SyncedQueryMeshNodes(
        IWorkspace workspace,
        object dataSourceId,
        string query,
        string? collectionName = null
    ) : base(workspace, dataSourceId, ws => BuildReadStream(ws, query, null!), collectionName)
    {
        // Re-bind the stream provider so the path-set fed back into _pathSet is
        // the one the live read uses (single subscription via StreamUpdates'
        // Replay(1).RefCount() in the base).
        Query = query;
    }

    public string Query { get; }

    /// <summary>
    /// Live snapshot of the paths currently matched by <see cref="Query"/>.
    /// Updated as the underlying <c>ObserveQuery</c> stream emits Initial /
    /// Added / Removed deltas. Read synchronously by the reducer to decide
    /// whether this source owns a given path.
    /// </summary>
    public ImmutableHashSet<string> CurrentPaths => _pathSet.Value;

    /// <summary>True when <paramref name="path"/> is in this source's live result set.</summary>
    public bool Owns(string path) => _pathSet.Value.Contains(path);

    /// <summary>
    /// Builds the read stream and tees its path-set snapshot into
    /// <paramref name="pathSet"/> so the reducer can answer
    /// <see cref="Owns"/> synchronously without a second subscription.
    /// </summary>
    private static IObservable<IEnumerable<MeshNode>> BuildReadStream(
        IWorkspace workspace, string query, BehaviorSubject<ImmutableHashSet<string>>? pathSet)
    {
        var provider = workspace.Hub.ServiceProvider.GetRequiredService<IMeshQueryProvider>();

        var pathSets = provider
            .ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery(query),
                workspace.Hub.JsonSerializerOptions)
            .Scan(
                ImmutableHashSet<string>.Empty,
                (set, change) => change.ChangeType switch
                {
                    QueryChangeType.Initial or QueryChangeType.Reset =>
                        change.Items.Select(n => n.Path).ToImmutableHashSet(),
                    QueryChangeType.Added =>
                        set.Union(change.Items.Select(n => n.Path)),
                    QueryChangeType.Removed =>
                        set.Except(change.Items.Select(n => n.Path)),
                    _ => set,
                })
            .DistinctUntilChanged(SetEquality.Instance)
            .Do(set => pathSet?.OnNext(set));

        return pathSets
            .Select(paths =>
                paths.IsEmpty
                    ? Observable.Return(Enumerable.Empty<MeshNode>())
                    : paths
                        .Select(path => GetNodeStream(workspace, path))
                        .CombineLatest()
                        .Select(values => values.Where(v => v is not null)!))
            .Switch()!;
    }

    private static IObservable<MeshNode?> GetNodeStream(IWorkspace workspace, string path)
    {
        if (string.Equals(workspace.Hub.Address.ToString(), path, StringComparison.Ordinal))
            return workspace.GetMeshNodeStream().Select(n => (MeshNode?)n);

        return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(path), new MeshNodeReference())
            .Select(c => c.Value);
    }

    /// <summary>
    /// Override the base stream provider so the path-set tee uses THIS
    /// instance's <see cref="_pathSet"/> (the constructor passes a sentinel
    /// because <c>this</c> isn't available before <c>base(...)</c>).
    /// </summary>
    protected override System.Threading.Tasks.Task<InstanceCollection> InitializeAsync(
        WorkspaceReference<InstanceCollection> reference,
        System.Threading.CancellationToken cancellationToken)
    {
        // Subscribe (silently) to the read stream so the BehaviorSubject is
        // primed with the initial path set even before any external reader
        // subscribes via StreamUpdates / GetStreamUpdates.
        Workspace.AddDisposable(BuildReadStream(Workspace, Query, _pathSet)
            .Subscribe(_ => { }, _ => { }));
        return base.InitializeAsync(reference, cancellationToken);
    }

    private sealed class SetEquality : IEqualityComparer<ImmutableHashSet<string>>
    {
        public static readonly SetEquality Instance = new();
        public bool Equals(ImmutableHashSet<string>? x, ImmutableHashSet<string>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SetEquals(y));
        public int GetHashCode(ImmutableHashSet<string> obj) => obj.Count;
    }
}
