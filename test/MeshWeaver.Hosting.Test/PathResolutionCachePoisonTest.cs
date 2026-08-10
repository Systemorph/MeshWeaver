using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Regression guard for the path-resolution cache amplification bug.
///
/// <para>The cache MUST store only the resolved <see cref="AddressResolution"/>
/// VALUE, never the in-flight resolution observable. An earlier design cached the
/// <c>Replay(1).AutoConnect(0)</c> promise and self-evicted only on
/// <c>onNext(null)</c> / <c>onError</c>. If the in-flight query never emitted its
/// Initial snapshot (the subscribe-handshake "dropped Full" race, likelier under
/// bulk load), the cached observable never emitted, errored, or completed — so the
/// self-eviction never fired and EVERY later resolution of that path replayed the
/// same dead observable forever. A one-call, self-healing race became a permanent
/// per-path stall — which then hung any downstream <c>mesh.Query(...).Take(1)</c>
/// that has no timeout (e.g. AI Export's subtree snapshot), timing out the whole
/// operation. This pins the fix deterministically: a hung first query must NOT
/// poison the path.</para>
/// </summary>
public class PathResolutionCachePoisonTest
{
    /// <summary>
    /// Minimal <see cref="IMeshQueryCore"/> whose Nth <c>Query</c> subscription is
    /// driven by an injected behaviour — lets a test make the FIRST resolution query
    /// hang (never emit) and later ones succeed. Hand-rolled (not NSubstitute) because
    /// <see cref="IMeshQueryCore"/> is internal and DynamicProxy can't proxy it.
    /// </summary>
    private sealed class SequencedQueryCore : IMeshQueryCore
    {
        private int _calls;
        private readonly Func<int, IObservable<QueryResultChange<MeshNode>>> _behaviour;
        public int Calls => _calls;

        public SequencedQueryCore(Func<int, IObservable<QueryResultChange<MeshNode>>> behaviour)
            => _behaviour = behaviour;

        public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
            => (IObservable<QueryResultChange<T>>)(object)_behaviour(Interlocked.Increment(ref _calls));
    }

    /// <summary>
    /// A WRITABLE partition provider (<see cref="IsReadOnly"/> false) whose partition
    /// definitively EXISTS. Both are required for the bare-partition-root placeholder
    /// synthesis (<c>PathResolutionService.SynthesizePartitionRoot</c>) to fire.
    /// </summary>
    private sealed class ExistingWritablePartitionProvider : IPartitionStorageProvider
    {
        public string Name => "test-writable";
        public bool IsReadOnly => false;
        public IStorageAdapter Adapter => throw new NotSupportedException("not used by path resolution");
        public IObservable<bool?> PartitionExists(string @namespace) => Observable.Return<bool?>(true);
    }

    private static (PathResolutionService Svc, SequencedQueryCore Query) BuildService(
        Func<int, IObservable<QueryResultChange<MeshNode>>> behaviour,
        IMeshChangeFeed? changeFeed = null,
        IEnumerable<IPartitionStorageProvider>? partitionProviders = null)
    {
        var hub = Substitute.For<IMessageHub>();
        var sp = Substitute.For<IServiceProvider>();
        hub.ServiceProvider.Returns(sp);
        hub.JsonSerializerOptions.Returns(new JsonSerializerOptions());
        // A registered change feed is what ENABLES caching (without it the service
        // resolves uncached). Real InProcessMeshChangeFeed — pass one in to drive
        // invalidation from the test; the default is never published to.
        sp.GetService(typeof(IMeshChangeFeed)).Returns(changeFeed ?? new InProcessMeshChangeFeed());
        // No AccessService → ImpersonateAsSystem() is a no-op (Disposable.Empty).

        var query = new SequencedQueryCore(behaviour);
        var svc = new PathResolutionService(hub, query,
            partitionProviders ?? Array.Empty<IPartitionStorageProvider>());
        return (svc, query);
    }

    private static QueryResultChange<MeshNode> Snapshot(params MeshNode[] items) =>
        new() { ChangeType = QueryChangeType.Initial, Items = items };

    private static IObservable<QueryResultChange<MeshNode>> InitialWith(string id, string ns) =>
        Answer(Snapshot(new MeshNode(id, ns) { NodeType = "Markdown" }));

    /// <summary>
    /// One Initial snapshot, then open forever. Live queries stay open after the Initial
    /// snapshot; <c>ResolveSegmentsCore</c> takes the first Initial and disposes, so the
    /// trailing Never is never observed.
    /// </summary>
    private static IObservable<QueryResultChange<MeshNode>> Answer(QueryResultChange<MeshNode> snapshot) =>
        Observable.Return(snapshot).Concat(Observable.Never<QueryResultChange<MeshNode>>());

    [Fact(Timeout = 30000)]
    public async Task HungFirstQuery_DoesNotPoisonCache()
    {
        // First resolution query hangs (Initial dropped → never emits); every later
        // query resolves "A/B". A promise-cache would pin the hung observable and the
        // second resolution would hang too; a value-cache stores nothing on the hang,
        // so the second resolution runs a fresh, succeeding query.
        var (svc, query) = BuildService(call =>
            call == 1
                ? Observable.Never<QueryResultChange<MeshNode>>()
                : InitialWith("B", "A"));

        // 1. First resolve hangs on the dropped Initial and its OWN wait gives up —
        //    exactly the pre-cache, self-healing behaviour of a single failed call.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            svc.ResolvePath("A/B").Take(1).Timeout(TimeSpan.FromSeconds(2)).ToTask());

        // 2. The hung first query must NOT have poisoned the path: a fresh resolution
        //    runs a new query and resolves. Against the old promise-cache this line
        //    times out (the dead observable is replayed) — that is the bug this pins.
        var resolution = await svc.ResolvePath("A/B")
            .Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();

        Assert.NotNull(resolution);
        Assert.Equal("A/B", resolution!.Prefix);
        Assert.True(query.Calls >= 2,
            "the second resolution must run a NEW query, not replay the hung first one");
    }

    [Fact(Timeout = 30000)]
    public async Task PositiveResolution_IsServedFromCache_OnSecondCall()
    {
        // Both queries would resolve, but the second call must be a cache hit that
        // does NOT invoke the query again (and emits synchronously).
        var (svc, query) = BuildService(_ => InitialWith("B", "A"));

        var first = await svc.ResolvePath("A/B").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A/B", first!.Prefix);
        var callsAfterFirst = query.Calls;

        AddressResolution? synchronous = null;
        using (svc.ResolvePath("A/B").Take(1).Subscribe(r => synchronous = r))
        {
            Assert.NotNull(synchronous); // warm hit replays synchronously on Subscribe
        }
        Assert.Equal("A/B", synchronous!.Prefix);
        Assert.Equal(callsAfterFirst, query.Calls); // no extra query on the cache hit
    }

    /// <summary>
    /// 🚨 A SYNTHESIZED bare-partition-root placeholder is a FABRICATION, not an observed
    /// fact, and must never be cached.
    ///
    /// <para>When a single-segment path matches nothing but its partition exists,
    /// <c>ResolveSegmentsCore</c> fabricates a placeholder <see cref="MeshNode"/> so the
    /// grain can still answer Ping (<c>PartitionRootActivationTest</c>). That placeholder
    /// carries <b>no NodeType and no Content</b>. Caching it pins the fabrication exactly the
    /// way caching a <c>null</c> pins a permanent 404 — the hazard the positive-only cache
    /// exists to avoid, wearing a disguise: once pinned, every later activation of the
    /// partition root routes on a NodeType-less node, so <c>EnrichWithNodeType</c> takes its
    /// silent <c>string.IsNullOrEmpty(nodeType)</c> branch and binds the mesh
    /// <c>DefaultNodeHubConfiguration</c> — the root then serves ONLY the framework's default
    /// layout areas and none of its real NodeType's, with no log line and no self-heal.
    /// That is the plugin gate's <c>Store/Catalog</c> RED: "No renderer is registered for
    /// area `Tests` on hub `Store`", listing exactly the 37 default areas.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SynthesizedPartitionRoot_IsNotCached()
    {
        // Query #1: the root's write has not reached the read index yet (eventually
        // consistent — the install writes the node through the owning hub's DEBOUNCED
        // persist). Query #2: the real, TYPED root is visible.
        var (svc, query) = BuildService(
            call => call == 1
                ? Answer(Snapshot())
                : Answer(Snapshot(new MeshNode("Store") { NodeType = "Store/Catalog" })),
            partitionProviders: new IPartitionStorageProvider[] { new ExistingWritablePartitionProvider() });

        var synthesized = await svc.ResolvePath("Store")
            .Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.NotNull(synthesized);
        Assert.Equal("Store", synthesized!.Prefix);
        // The fabrication: a bare node with no NodeType — enough to answer Ping, never
        // enough to bind the root's real hub configuration.
        Assert.Null(synthesized.Node!.NodeType);

        // The fabrication must NOT have been pinned: the next resolution re-queries and
        // returns the REAL typed root. Before the fix this returns the cached placeholder
        // (NodeType null) forever — nothing ever invalidates it, because the create event
        // that would have fired already fired while the first query was in flight.
        var real = await svc.ResolvePath("Store")
            .Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.NotNull(real);
        Assert.Equal("Store", real!.Prefix);
        Assert.Equal("Store/Catalog", real.Node!.NodeType);
        Assert.True(query.Calls >= 2, "the synthesized placeholder must not be served from cache");
    }

    /// <summary>
    /// 🚨 Fill-after-invalidate: a resolution computed from a PRE-change snapshot must never
    /// be committed to the cache after the change event for that key has already run.
    ///
    /// <para>The invalidation hook removes matching keys from the cache — but a resolution
    /// that is still IN FLIGHT is not in the cache yet, so the event sweeps nothing and the
    /// query's stale answer is added AFTERWARDS. Nothing removes it again (the write that
    /// would have invalidated it has already happened), so the path is pinned to the
    /// pre-change answer for the rest of the process. Here <c>A/B</c> is created while its
    /// resolution query is open; the query answers with the pre-create snapshot (only the
    /// ancestor <c>A</c> matched → remainder <c>B</c> → routing NACKs NotFound), and every
    /// later resolution must NOT be served that answer.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task InvalidationDuringInFlightQuery_DoesNotPinThePreChangeAnswer()
    {
        var feed = new InProcessMeshChangeFeed();
        // Query #1 is held open by the test so the change event lands mid-flight.
        var inFlight = new Subject<QueryResultChange<MeshNode>>();
        var (svc, query) = BuildService(
            call => call == 1 ? inFlight : InitialWith("B", "A"),
            changeFeed: feed);

        AddressResolution? stale = null;
        using var subscription = svc.ResolvePath("A/B").Take(1).Subscribe(r => stale = r);

        // The node is CREATED while the resolution query is open. The cache holds nothing
        // for "A/B" yet, so this invalidation sweeps nothing.
        feed.Publish(MeshChangeEvent.Created(new MeshNode("B", "A") { NodeType = "Markdown" }));

        // …and only now does the in-flight query answer, from its PRE-create snapshot:
        // only the ancestor "A" exists, so "A/B" resolves to prefix "A" remainder "B".
        inFlight.OnNext(Snapshot(new MeshNode("A")));
        Assert.NotNull(stale);
        Assert.Equal("A", stale!.Prefix);
        Assert.Equal("B", stale.Remainder);

        // That answer must not have been pinned. A fresh resolution re-queries and finds
        // the created node. Before the fix this replays the stale ancestor match forever —
        // the "row was written and then answered `No node found` forever" failure mode.
        var fresh = await svc.ResolvePath("A/B")
            .Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.NotNull(fresh);
        Assert.Equal("A/B", fresh!.Prefix);
        Assert.Null(fresh.Remainder);
        Assert.True(query.Calls >= 2, "the pre-change answer must not be served from cache");
    }

    /// <summary>
    /// 🚨 Issue #1172 — an in-place UPDATE must not evict the route-shape cache.
    ///
    /// <para>An Updated event cannot change any path's resolution SHAPE: the node existed at that
    /// path before the write and still does (moves publish Deleted+Created). Yet the sweep used to
    /// REMOVE the entry, so a hot-WRITTEN path — a compile activity receiving one routed
    /// <c>PatchDataRequest</c> per appended log line — paid a fresh storage query for EVERY routed
    /// message, each holding a RoutingGrain in-flight slot for the query's duration. During the
    /// boot NodeType bake that saturated the silo's routing window at 64, the bake's own stream
    /// waits timed out behind the saturated router, and the bake inflated ~10× (pods not-Ready for
    /// 80+ minutes). Route-shape resolution (<see cref="IPathResolver.ResolveRoute"/>) must stay a
    /// warm cache hit across Updated writes; node-reading resolution
    /// (<see cref="IPathResolver.ResolvePath"/>) must re-query for the fresh node.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UpdatedNode_KeepsRouteResolutionWarm_WhileNodeReadersRefresh()
    {
        var feed = new InProcessMeshChangeFeed();
        var (svc, query) = BuildService(_ => InitialWith("B", "A"), changeFeed: feed);

        var first = await svc.ResolvePath("A/B").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A/B", first!.Prefix);
        var callsAfterFill = query.Calls;

        // The hot-write: the node at A/B is updated in place (an activity-log append).
        feed.Publish(MeshChangeEvent.Updated(new MeshNode("B", "A") { NodeType = "Markdown" }));

        // ROUTE-shape resolution is served synchronously from the cache — NO new query. This is
        // the silo router's per-message lookup; before the fix the Updated sweep removed the
        // entry and this line ran a fresh storage query (the #1172 amplifier).
        AddressResolution? routed = null;
        using (svc.ResolveRoute("A/B").Take(1).Subscribe(r => routed = r))
            Assert.NotNull(routed);
        Assert.Equal("A/B", routed!.Prefix);
        Assert.Null(routed.Remainder);
        // The stale entry must not pin the superseded node (whole Content) in memory — the
        // route-only answer carries no node.
        Assert.Null(routed.Node);
        Assert.Equal(callsAfterFill, query.Calls);

        // A NODE-reading caller (grain activation) sees the same entry as a miss and re-queries.
        var refreshed = await svc.ResolvePath("A/B").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A/B", refreshed!.Prefix);
        Assert.NotNull(refreshed.Node);
        Assert.True(query.Calls > callsAfterFill, "a node-reading caller must re-query after Updated");

        // …and its fresh commit repairs the entry for node readers too: the next ResolvePath is
        // a warm hit again.
        var callsAfterRefresh = query.Calls;
        AddressResolution? warmAgain = null;
        using (svc.ResolvePath("A/B").Take(1).Subscribe(r => warmAgain = r))
            Assert.NotNull(warmAgain);
        Assert.Equal(callsAfterRefresh, query.Calls);
    }

    /// <summary>
    /// 🚨 Issue #1172, the storm shape: on a path written CONTINUOUSLY, an Updated lands during
    /// every resolution query, so dropping the fill claim on Updated (the old behaviour) meant
    /// the cache could NEVER commit an entry for exactly the paths that need it most — every
    /// routed message paid a fresh storage query forever. The fill must still commit
    /// (stale-marked: its route shape is valid), so the NEXT routed message is a cache hit.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UpdatedDuringInFlightFill_StillCommitsTheRouteShape()
    {
        var feed = new InProcessMeshChangeFeed();
        var inFlight = new Subject<QueryResultChange<MeshNode>>();
        var (svc, query) = BuildService(
            call => call == 1 ? inFlight : InitialWith("B", "A"),
            changeFeed: feed);

        AddressResolution? first = null;
        using var subscription = svc.ResolveRoute("A/B").Take(1).Subscribe(r => first = r);

        // The node is UPDATED while the resolution query is open — the per-log-line write storm.
        feed.Publish(MeshChangeEvent.Updated(new MeshNode("B", "A") { NodeType = "Markdown" }));

        inFlight.OnNext(Snapshot(new MeshNode("B", "A") { NodeType = "Markdown" }));
        Assert.NotNull(first);
        Assert.Equal("A/B", first!.Prefix);
        var callsAfterFill = query.Calls;

        // The route shape committed despite the mid-flight Updated: the next routed message is a
        // synchronous cache hit. Before the fix the Updated dropped the claim, nothing was
        // committed, and this ran query #2 — as would every routed message after it.
        AddressResolution? second = null;
        using (svc.ResolveRoute("A/B").Take(1).Subscribe(r => second = r))
            Assert.NotNull(second);
        Assert.Equal("A/B", second!.Prefix);
        Assert.Equal(callsAfterFill, query.Calls);

        // But the mid-flight Updated must have marked the committed entry stale for NODE readers:
        // ResolvePath re-queries rather than serving a node snapshot from a pre-update query.
        var refreshed = await svc.ResolvePath("A/B").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A/B", refreshed!.Prefix);
        Assert.True(query.Calls > callsAfterFill,
            "a node-reading caller must not be served the pre-update node snapshot");
    }

    /// <summary>
    /// Created and Deleted CAN change a path's resolution shape (deepen it / remove it), so they
    /// must keep invalidating the route-shape view too — the #1172 fix is scoped to Updated only.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreatedAndDeleted_StillInvalidateRouteResolution()
    {
        var feed = new InProcessMeshChangeFeed();
        // Query #1: only the ancestor A exists → A/B/C resolves to prefix A. Query #2 (after
        // Created(A/B)): the deeper node exists → prefix A/B. Query #3 (after Deleted(A/B)):
        // back to prefix A.
        var (svc, query) = BuildService(call => call switch
        {
            1 => Answer(Snapshot(new MeshNode("A"))),
            2 => Answer(Snapshot(new MeshNode("A"), new MeshNode("B", "A") { NodeType = "Markdown" })),
            _ => Answer(Snapshot(new MeshNode("A")))
        }, changeFeed: feed);

        var shallow = await svc.ResolveRoute("A/B/C").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A", shallow!.Prefix);

        // Created(A/B) deepens the resolution of A/B/C — the cached route entry must go.
        feed.Publish(MeshChangeEvent.Created(new MeshNode("B", "A") { NodeType = "Markdown" }));
        var deeper = await svc.ResolveRoute("A/B/C").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A/B", deeper!.Prefix);
        Assert.True(query.Calls >= 2, "Created must invalidate the route-shape cache");

        // Deleted(A/B) shallows it again.
        feed.Publish(MeshChangeEvent.Deleted("A/B"));
        var shallowAgain = await svc.ResolveRoute("A/B/C").Take(1).Timeout(TimeSpan.FromSeconds(5)).ToTask();
        Assert.Equal("A", shallowAgain!.Prefix);
        Assert.True(query.Calls >= 3, "Deleted must invalidate the route-shape cache");
    }
}
