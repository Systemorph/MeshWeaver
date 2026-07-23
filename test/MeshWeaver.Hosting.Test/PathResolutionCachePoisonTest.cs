using System;
using System.Collections.Generic;
using System.Reactive.Linq;
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

    private static (PathResolutionService Svc, SequencedQueryCore Query) BuildService(
        Func<int, IObservable<QueryResultChange<MeshNode>>> behaviour)
    {
        var hub = Substitute.For<IMessageHub>();
        var sp = Substitute.For<IServiceProvider>();
        hub.ServiceProvider.Returns(sp);
        hub.JsonSerializerOptions.Returns(new JsonSerializerOptions());
        // A registered change feed is what ENABLES caching (without it the service
        // resolves uncached). Real InProcessMeshChangeFeed — no writes fire in this test.
        sp.GetService(typeof(IMeshChangeFeed)).Returns(new InProcessMeshChangeFeed());
        // No AccessService → ImpersonateAsSystem() is a no-op (Disposable.Empty).

        var query = new SequencedQueryCore(behaviour);
        var svc = new PathResolutionService(hub, query, Array.Empty<IPartitionStorageProvider>());
        return (svc, query);
    }

    private static IObservable<QueryResultChange<MeshNode>> InitialWith(string id, string ns) =>
        Observable
            .Return(new QueryResultChange<MeshNode>
            {
                ChangeType = QueryChangeType.Initial,
                Items = new List<MeshNode> { new(id, ns) { NodeType = "Markdown" } }
            })
            // Live queries stay open after the Initial snapshot; ResolveSegmentsCore
            // takes the first Initial and disposes, so the trailing Never is never observed.
            .Concat(Observable.Never<QueryResultChange<MeshNode>>());

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
}
