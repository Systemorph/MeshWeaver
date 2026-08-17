#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The REPLY path for requests issued on a pod's ROOT MESH HUB (<c>mesh/{id}</c>) — the exact
/// shape of core#694 layer 2. The static-content endpoint (and anything else still posting from
/// the root hub) issues <c>GetDataRequest</c>-style reads whose RESPONSE is a separate post
/// targeted back at <c>mesh/{id}</c>. Same-silo that reply hits the routing service's local
/// stream table; CROSS-silo it reached the RoutingGrain, and because <c>mesh</c> was not a
/// stream-routed address type it fell into the grain path — a black hole. The observable
/// symptom in prod: at 2 replicas ~half of all /static requests hang silently (35/60 measured
/// on memex, 2026-07-29), ratio tracking which silo owns each partition's per-node hub grain.
///
/// <para>The repro is DETERMINISTIC without knowing the grain placement: the same read is
/// issued from BOTH silos' root mesh hubs — whichever silo does not own the per-node grain is
/// guaranteed to exercise the cross-silo reply.</para>
///
/// <para>🚨 <b>READ THIS BEFORE TRUSTING THIS CLASS AS THE /api/content GUARD — it is not, any
/// more.</b> The paragraph above still describes the ORIGINAL intent, but <c>2c796d297</c>
/// (2026-08-10) moved <c>GetMeshNode</c>/<c>GetMeshNodeOutcome</c> onto
/// <c>MeshExtensions.NodeOperationIssuingHub</c>, so the fact below now exercises the
/// <c>portal/nodeops-…</c> reply path and no longer posts from <c>mesh/{id}</c> at all. That is
/// worth keeping — but it silently stopped covering the static-content endpoint it names, and
/// issue #1729 is what that cost: <c>ContentFileResolver.Resolve</c> was left posting from the
/// router and hung ~half of all <c>/api/content</c> requests on the 2-replica memex-cloud portal.
/// The guard for that now lives in
/// <c>MeshWeaver.Hosting.Blazor.Test.ContentRouteIssuingHubTest</c>, which asserts the SENDER the
/// owning node hub sees — deliberately NOT here, because an IN-PROCESS <c>TestCluster</c> does not
/// reproduce the cross-PROCESS reply loss: a two-silo version of that assertion passes with and
/// without the fix, which is worse than no test. Do not "restore" the coverage by adding a
/// content-route fact to this class without first proving it FAILS on the unfixed code.</para>
/// </summary>
public class OrleansCrossSiloReplyTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;

    public OrleansCrossSiloReplyTest(TwoSiloCacheUpdateFixture fixture)
    {
        _fixture = fixture;
    }

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services
            .GetRequiredService<IMessageHub>();

    /// <summary>
    /// A node read issued on EACH silo's root mesh hub must receive its reply — including the
    /// silo that does NOT own the target's per-node hub grain, whose reply crosses silos.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RootMeshHubRead_ReceivesItsReply_FromBothSilos()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(100)).Token;
        var cluster = _fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the repro needs two silos");

        var hubA = SiloMeshHub(cluster, 0);
        var hubB = SiloMeshHub(cluster, 1);

        // One node; its per-node hub grain hashes onto exactly one silo, so of the two reads
        // below one is same-silo and one is cross-silo — no placement knowledge needed.
        var ns = $"xsilo-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        var create = await hubA
            .Observe(new CreateNodeRequest(new MeshNode("Doc", ns)
            {
                NodeType = "Markdown",
                Name = "Doc",
                State = MeshNodeState.Active,
            }), o => o.WithTarget(hubA.Address))
            .FirstAsync().ToTask(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        // Same-silo leg first: proves the node reads fine at all, so a cross-silo failure
        // below can only be the REPLY path, not the node.
        foreach (var (hub, label) in new[] { (hubA, "silo A"), (hubB, "silo B") })
        {
            var node = await hub.GetMeshNode(path, TimeSpan.FromSeconds(20))
                .FirstAsync()
                .ToTask(ct);
            node.Should().NotBeNull(
                $"the read issued on {label}'s root mesh hub must receive its reply — "
                + "when this is the non-owning silo, the reply crosses silos, and a hang here "
                + "IS core#694 layer 2 (the memex 35/60 static-content failure at 2 replicas)");
            node!.Path.Should().Be(path);
        }
    }

}

/// <summary>
/// ROLLOVER variant — its OWN class and therefore its OWN two-silo cluster: the test STOPS the
/// secondary silo, so sharing a fixture with the steady-state test would starve it of its
/// second silo (exactly what the first cut did — xUnit ran this class-mate first and the
/// steady-state test found a one-silo cluster).
/// </summary>
public class OrleansCrossSiloReplyRolloverTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture _fixture;

    public OrleansCrossSiloReplyRolloverTest(TwoSiloCacheUpdateFixture fixture)
    {
        _fixture = fixture;
    }

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services
            .GetRequiredService<IMessageHub>();

    /// <summary>
    /// ROLLOVER: after the secondary silo leaves (a rolling deploy's constant reality — every
    /// deploy has a two-silo window and then one leaves), a read from the surviving silo must
    /// still complete: if the departed silo owned the per-node grain, Orleans reactivates it on
    /// the survivor; the reply is then same-silo. Bounded — a hang here is a failed rollover.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task RootMeshHubRead_SurvivesSecondarySiloDeparture()
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(160)).Token;
        var cluster = _fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the rollover repro needs two silos");

        var hubA = SiloMeshHub(cluster, 0);

        var ns = $"rollover-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        var create = await hubA
            .Observe(new CreateNodeRequest(new MeshNode("Doc", ns)
            {
                NodeType = "Markdown",
                Name = "Doc",
                State = MeshNodeState.Active,
            }), o => o.WithTarget(hubA.Address))
            .FirstAsync().ToTask(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        // Touch the node once so its grain is activated (on whichever silo).
        var before = await hubA.GetMeshNode(path, TimeSpan.FromSeconds(20)).FirstAsync().ToTask(ct);
        before.Should().NotBeNull("the node must read before the rollover");

        // The rollover: the secondary silo leaves gracefully (the rolling-deploy shape).
        await cluster.StopSecondarySilosAsync();

        // The surviving silo must still serve the read — grain reactivation on the survivor,
        // bounded. Retry across the reactivation window; a persistent hang is the defect.
        var after = await Observable
            .Interval(TimeSpan.FromSeconds(2)).StartWith(0L)
            .SelectMany(_ => hubA.GetMeshNode(path, TimeSpan.FromSeconds(15),
                    ReadTimeoutBehavior.EmitNull)
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(90))
            .ToTask(ct);
        after!.Path.Should().Be(path,
            "after the secondary silo departs, the surviving silo must re-own the grain and "
            + "serve the read — a hang here is a failed rollover");
    }
}
