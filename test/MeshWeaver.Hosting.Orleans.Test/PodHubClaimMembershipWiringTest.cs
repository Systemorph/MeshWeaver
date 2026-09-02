#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>The WIRING half of #2938, on a real cluster.</b>
///
/// <para><see cref="PodHubClaimReassertionTest"/> pins the POLICY — a landed claim is re-asserted
/// when a membership change arrives — with a feed the test pushes. That is the decision, and it is
/// where the regression would be reintroduced. It cannot, however, see the other way this fix dies:
/// the silo registering an <see cref="IClusterMembershipFeed"/> that never actually emits, because
/// nothing subscribed to Orleans' silo-status oracle. A policy test and a registration check would
/// both stay green through that — the guard-asserting-config shape.</para>
///
/// <para>So this asserts the OUTCOME on the real cluster machinery: a genuine membership change (a
/// silo joining) reaches the feed, and the addresses claimed before it are still reachable across
/// silos afterwards. Both halves are required — the first alone would pass on a feed nobody acts
/// on, the second alone would pass on a cluster where nothing was ever at risk.</para>
/// </summary>
public class PodHubClaimMembershipWiringTest : IClassFixture<TwoSiloCacheUpdateFixture>
{
    private readonly TwoSiloCacheUpdateFixture fixture;

    public PodHubClaimMembershipWiringTest(TwoSiloCacheUpdateFixture fixture)
    {
        this.fixture = fixture;
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private static IMessageHub SiloMeshHub(TestCluster cluster, int index)
        => SiloServices(cluster, index).GetRequiredService<IMessageHub>();

    private static OrleansRoutingService Routing(TestCluster cluster, int index)
        => (OrleansRoutingService)SiloServices(cluster, index).GetRequiredService<IRoutingService>();

    /// <summary>
    /// 🚨 THE PIN. A real silo joining the cluster must reach the feed, and an address claimed
    /// before that join must still be deliverable across silos after it.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASiloJoining_ReachesTheFeed_AndClaimedAddressesStayReachable()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2,
            "the delivery has to CROSS silos, or the sender's own local route short-circuits and "
            + "neither the router nor the claim is involved at all");

        // The feed the SILO registers — not one this test builds. Resolving it is also what makes
        // it subscribe to the oracle, exactly as the routing service's construction does in
        // production.
        var feed = SiloServices(cluster, 0).GetService<IClusterMembershipFeed>();
        feed.Should().NotBeNull(
            "a silo must register the membership feed — without it the pod-hub claim silently "
            + "reverts to the one-shot assertion that is #2938's root cause");

        var changes = new ConcurrentQueue<long>();
        using var membership = feed!.Changes.Subscribe(changes.Enqueue);

        // An address claimed BEFORE the membership change — the population at risk.
        var address = new Address("client", $"wiring-{Guid.NewGuid():N}");
        var inbox = new ConcurrentQueue<IMessageDelivery>();
        using var registration = Routing(cluster, 0).RegisterStream(address, (d, _) =>
        {
            inbox.Enqueue(d);
            return Observable.Return(d);
        });

        await WaitUntil(
            () =>
            {
                SiloMeshHub(cluster, 1).Post(new PingRequest(), o => o.WithTarget(address));
                return !inbox.IsEmpty;
            },
            "the claim must be live BEFORE the membership change, or this test would prove nothing "
            + "about surviving one",
            ct);

        // A REAL membership change: a silo joins, which is what a rolling deploy and every KEDA
        // scale-up do, and what re-partitions Orleans' grain directory.
        await cluster.StartAdditionalSiloAsync();

        await WaitUntil(() => !changes.IsEmpty,
            "a silo joining must reach the feed. Silence here is the failure mode a registration "
            + "check cannot see: the feed exists, nothing ever subscribed it to Orleans' silo-status "
            + "oracle, and every claim is back to being asserted exactly once",
            ct);

        // …and the address is still reachable across silos afterwards.
        inbox.Clear();
        await WaitUntil(
            () =>
            {
                SiloMeshHub(cluster, 1).Post(new PingRequest(), o => o.WithTarget(address));
                return !inbox.IsEmpty;
            },
            "a hub whose claim was made before the cluster's membership moved must still be "
            + "reachable by directed grain call afterwards — that is the whole of #2938",
            ct);
    }

    private static async Task WaitUntil(Func<bool> condition, string because, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"Timed out after {Budget}: {because}");
    }
}
