#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.TestingHost;
using Xunit;
using MeshWeaver.Fixture;

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
/// (2026-08-10) moved <c>GetMeshNode</c>/<c>GetMeshNodeOutcome</c> off <c>mesh/{id}</c> onto a
/// dedicated off-router hub — <c>portal/nodeops-…</c> then, and <c>portal/reads-…</c> since #2901
/// split the READ seam out of the node-operation one
/// (<c>MeshExtensions.ReadIssuingHub</c>) — so the fact below exercises THAT hub's reply path and
/// no longer posts from <c>mesh/{id}</c> at all. That is
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
            .FirstAsync().Await(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        // Same-silo leg first: proves the node reads fine at all, so a cross-silo failure
        // below can only be the REPLY path, not the node.
        foreach (var (hub, label) in new[] { (hubA, "silo A"), (hubB, "silo B") })
        {
            var node = await hub.GetMeshNode(path, TimeSpan.FromSeconds(20))
                .FirstAsync()
                .Await(ct);
            node.Should().NotBeNull(
                $"the read issued on {label}'s root mesh hub must receive its reply — "
                + "when this is the non-owning silo, the reply crosses silos, and a hang here "
                + "IS core#694 layer 2 (the memex 35/60 static-content failure at 2 replicas)");
            node!.Path.Should().Be(path);
        }
    }

    /// <summary>
    /// 🚨 <b>THE INPUT THIS CLASS LOST — issue #1742.</b> The fact above no longer posts from
    /// <c>mesh/{id}</c> at all (<c>2c796d297</c> moved <c>GetMeshNode</c> onto a dedicated
    /// off-router hub, <c>portal/reads-…</c> today), so the class named for the root-mesh-hub reply leg stopped
    /// exercising it — the gate silently stopped testing its own input, and #1729 is what that cost.
    /// This restores it in the smallest possible shape.
    ///
    /// <para><c>PingRequest</c> is answered by EVERY hub (<c>MessageHub.HandlePingRequest</c>) and
    /// its answer is a separate post targeted back at the SENDER — so a ping issued on
    /// <c>mesh/{id}</c> against a per-node hub grain is precisely the exchange this issue is named
    /// for, with nothing else in the way. Issuing from BOTH silos' root hubs makes the repro
    /// deterministic without knowing the placement: the node's grain hashes onto exactly ONE silo,
    /// so one of the two legs is guaranteed to be the cross-silo reply.</para>
    ///
    /// <para>Deliberately a REQUEST/RESPONSE round trip and not a forward delivery: the forward leg
    /// is an Orleans grain call that is retried and NACK'd, while the reply leg is the one that had
    /// no delivery guarantee and no failure signal. That asymmetry IS the bug this issue reports.</para>
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task RootMeshHubRequest_ReceivesItsReply_FromBothSilos()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var ct = deadline.Token;
        var cluster = _fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "one of the two legs has to CROSS silos");

        var hubA = SiloMeshHub(cluster, 0);
        var hubB = SiloMeshHub(cluster, 1);

        var ns = $"rootping-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        var create = await hubA
            .Observe(new CreateNodeRequest(new MeshNode("Doc", ns)
            {
                NodeType = "Markdown",
                Name = "Doc",
                State = MeshNodeState.Active,
            }), o => o.WithTarget(hubA.Address))
            .FirstAsync().Await(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        foreach (var (hub, label) in new[] { (hubA, "silo A"), (hubB, "silo B") })
        {
            var pong = await hub.Observe(new PingRequest(), o => o.WithTarget((Address)path))
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(40))
                .Await(ct);

            pong.Message.Should().NotBeNull(
                $"the ping issued on {label}'s ROOT MESH HUB must receive its PingResponse. When "
                + "this is the non-owning silo the reply crosses silos back to mesh/{id}, which is "
                + "the exchange issue #1742 is named for — a hang here is that defect, live");
        }
    }

    /// <summary>
    /// 🚨 <b>WHICH TRANSPORT the reply above actually took — and this is the fact that decides
    /// whether #1742 is fixed or merely quiet.</b>
    ///
    /// <para>Cross-silo delivery to a pod-process hub has two transports. The directed
    /// <c>IPodHubGrain</c> call either LANDS or ANSWERS; the Orleans memory-stream publish is
    /// fire-and-forget over a NON-DURABLE queue whose grain dies with its silo, so it can succeed and
    /// discard. The router silently prefers the first and falls back to the second, which means the
    /// round trip above passes either way — and would go on passing if the root hub's claim quietly
    /// stopped landing.</para>
    ///
    /// <para>That is not hypothetical for THIS address specifically. Every other stream-routed hub
    /// claims itself from application code, but <c>mesh/{id}</c> is registered by
    /// <c>RootMeshHubReplyStreamService</c> — an <c>IHostedService</c> running at host start — and
    /// <c>OrleansRoutingService.AttachPodHub</c> is best-effort with a bounded (~3 s) claim retry,
    /// while the stream subscription it sits beside is ordered on a 120 s
    /// <c>OrleansStreamingReadiness</c> gate. A claim that failed to land degrades SILENTLY to the
    /// stream — i.e. straight back onto the transport this issue is about — and nothing else would
    /// notice.</para>
    ///
    /// <para>So this asks the transport directly, from the silo that does NOT host the address:
    /// <c>PodHubNotHereException</c> means no process claims it and every cross-silo reply to it is
    /// riding the memory stream.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RootMeshHub_IsClaimedForItsProcess_SoItsRepliesTakeTheDirectedTransport()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var cluster = _fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2,
            "asking from the OWNING silo would be answered by its own local route and prove nothing");

        var rootA = SiloMeshHub(cluster, 0).Address;

        // Silo B's grain factory, so the call is genuinely cross-silo. A PingRequest is inert on
        // arrival — every hub answers it — so this probes the transport without leaving state behind.
        var grains = ((InProcessSiloHandle)cluster.Silos[1]).SiloHost.Services
            .GetRequiredService<IGrainFactory>();

        var probe = new MessageDelivery<PingRequest>(
            new PingRequest(),
            new PostOptions(rootA).WithTarget(rootA),
            System.Text.Json.JsonSerializerOptions.Default);

        var delivered = await grains.GetGrain<IPodHubGrain>(rootA.ToString())
            .Deliver(probe)
            .WaitAsync(TimeSpan.FromSeconds(30), ct);

        delivered.State.Should().Be(MessageDeliveryState.Forwarded,
            $"the root mesh hub {rootA} must be claimed for its own process, so that a cross-silo "
            + "reply to it is a directed grain call with an outcome rather than a publish onto a "
            + "non-durable memory stream that succeeds whether or not anybody is listening. A "
            + "PodHubNotHereException here means the claim never landed and this hub is back on the "
            + "transport issue #1742 reports — silently");
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
            .FirstAsync().Await(ct);
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        // Touch the node once so its grain is activated (on whichever silo).
        var before = await hubA.GetMeshNode(path, TimeSpan.FromSeconds(20)).FirstAsync().Await(ct);
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
            .Await(ct);
        after!.Path.Should().Be(path,
            "after the secondary silo departs, the surviving silo must re-own the grain and "
            + "serve the read — a hang here is a failed rollover");
    }
}
