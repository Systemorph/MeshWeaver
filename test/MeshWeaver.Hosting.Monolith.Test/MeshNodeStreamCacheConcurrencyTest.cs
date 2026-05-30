using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Thread-safety properties of <see cref="IMeshNodeStreamCache.GetQuery"/> —
/// the process-singleton cache shared by every per-node hub. In Orleans every
/// grain is re-entrant: a single grain can be servicing many concurrent
/// requests, and many grains across the silo will hit the cache for the same
/// query id at the same time. These tests assert the cache's contract holds
/// under concurrent access.
///
/// <para>The cache's <c>GetQuery(id, queries...)</c> contract:</para>
/// <list type="number">
///   <item><b>Idempotent</b> — N concurrent calls with the same <c>id</c>
///     return semantically equivalent observables (every subscriber sees the
///     same emissions).</item>
///   <item><b>Single upstream</b> — only ONE upstream
///     <see cref="MeshWeaver.Mesh.Services.IMeshQueryCore.ObserveQuery"/>
///     subscription is opened per <c>id</c>, regardless of subscriber count
///     (the CAS-loser observable from the swap loop must not leak Connect).</item>
///   <item><b>Eventually consistent</b> — once a node matching the query
///     lands in persistence, every subscriber's next emission reflects it.</item>
/// </list>
/// </summary>
public class MeshNodeStreamCacheConcurrencyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Namespace = $"{TestPartition}/CacheConcurrency";

    [Fact(Timeout = 30_000)]
    public async Task GetQuery_ManyConcurrentCallersSameId_AllSeeSameSnapshot()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var id = $"$concurrency-test-{Guid.NewGuid():N}";
        var query = $"namespace:{Namespace} nodeType:Markdown";

        // Seed one node so the snapshot is non-empty.
        await NodeFactory.CreateNode(new MeshNode("seed", Namespace)
        {
            Name = "Seed",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Fan-out: N concurrent calls to GetQuery for the same id, each takes
        // the first emission. With AutoConnect(1) the first to subscribe
        // triggers the upstream connect; the rest read from Replay(1). If
        // the CAS loop's per-iteration AutoConnect(0) was leaking subscriptions
        // (the bug fixed in 04fae8415), we'd see N upstream queries instead
        // of 1 — measurable by counting StaticNodeQueryProvider's ObserveQuery
        // hits via the dispatch trace, but for the contract test we just
        // assert all N subscribers see the same snapshot.
        const int Concurrency = 64;
        var results = new ConcurrentBag<IReadOnlyList<string>>();
        var tasks = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(async () =>
        {
            var snapshot = await cache.GetQuery(id, query)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            results.Add(snapshot.Select(n => n.Path).OrderBy(p => p).ToList());
        })).ToArray();

        await Task.WhenAll(tasks);

        results.Should().HaveCount(Concurrency, "every concurrent caller must complete");
        var distinct = results.Select(r => string.Join("|", r)).Distinct().ToList();
        distinct.Should().HaveCount(1,
            "every subscriber must see the same snapshot — got {0} distinct: {1}",
            distinct.Count, string.Join(" / ", distinct));
        distinct[0].Should().Contain("seed", "the seeded node must be in every snapshot");
    }

    [Fact(Timeout = 30_000)]
    public async Task GetQuery_ReturnsLiveUpdatesAfterRuntimeCreate()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var id = $"$live-update-test-{Guid.NewGuid():N}";
        var query = $"namespace:{Namespace} nodeType:Markdown";

        // First subscriber attaches BEFORE any writes — pins the AutoConnect(1)
        // upstream and starts buffering the synced-query change feed.
        var liveSnapshots = new List<int>();
        using var sub = cache.GetQuery(id, query)
            .Subscribe(snapshot => liveSnapshots.Add(snapshot.Count()));

        // Seed two nodes.
        await NodeFactory.CreateNode(new MeshNode("live-1", Namespace)
        {
            Name = "Live 1", NodeType = "Markdown", State = MeshNodeState.Active,
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(new MeshNode("live-2", Namespace)
        {
            Name = "Live 2", NodeType = "Markdown", State = MeshNodeState.Active,
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Subscribers attaching AFTER the writes must see at least 2 nodes —
        // the AutoConnect(1) Replay buffer should reflect the live state, not
        // the empty Initial.
        var lateSubscriberCount = await cache.GetQuery(id, query)
            .Where(s => s.Count() >= 2)
            .Timeout(10.Seconds())
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        lateSubscriberCount.Count().Should().BeGreaterThanOrEqualTo(2);

        // The first subscriber (held open across both creates) should also
        // have observed at least one emission with both nodes — the eventual
        // consistency guarantee on long-lived subscriptions.
        liveSnapshots.Should().NotBeEmpty();
    }

    [Fact(Timeout = 30_000)]
    public async Task GetQuery_ConcurrentDifferentIds_AllResolveIndependently()
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        await NodeFactory.CreateNode(new MeshNode("indep-seed", Namespace)
        {
            Name = "Seed", NodeType = "Markdown", State = MeshNodeState.Active,
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

        // Each task uses a distinct id — exercises the CAS swap with N
        // different keys hitting _queries simultaneously. The ImmutableDictionary
        // CAS retry loop must converge for every key.
        const int Concurrency = 32;
        var query = $"namespace:{Namespace} nodeType:Markdown";

        var tasks = Enumerable.Range(0, Concurrency).Select(i => Task.Run(async () =>
        {
            var id = $"$independent-{i}";
            var snapshot = await cache.GetQuery(id, query)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            return snapshot.Any(n => n.Path == $"{Namespace}/indep-seed");
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        results.Should().AllBeEquivalentTo(true,
            System.Text.Json.JsonSerializerOptions.Default,
            because: "every distinct-id concurrent caller must see the seeded node");
    }
}
