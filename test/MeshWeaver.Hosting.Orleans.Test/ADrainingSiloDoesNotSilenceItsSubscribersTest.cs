#pragma warning disable CS1591

using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5256 (with #1114, #5230 and #2254): a subscriber of a per-node owner hub went silent for
/// 30–60 s, and every sample sat inside a ROLL window.
///
/// <para><b>The shape reproduced.</b> Two silos. The owner's grain lives on silo B, the subscriber on
/// silo A. Silo B is told to stop (<see cref="IHostApplicationLifetime.StopApplication"/>, the call
/// the runtime makes on SIGTERM) and — as on a terminating pod, for its whole grace period — it
/// LINGERS: still Active in membership, its grains still active, so the directory still sends a
/// fresh subscribe to the owner on B.</para>
///
/// <para><b>What was measured before the fix.</b> The owner on B answered — a <c>SubscribeAck</c>
/// and the first Full within a millisecond — and B's router refused both ("Host is shutting down,
/// cannot route to cache/…"), because a leaving host refuses every outbound delivery. Nothing told
/// the subscriber anything; it waited out its own budget. The hypothesis on #5256 (an owner
/// deactivated between the ack and its first frame) was not what happened: the owner never
/// deactivated, and its frame was produced — it just could not leave the process.</para>
///
/// <para><b>The contract.</b> The subscriber gets the node — from the owner or from the next
/// activation — or a retryable failure, promptly, while the leaving silo is still lingering.</para>
/// </summary>
public class ADrainingSiloDoesNotSilenceItsSubscribersTest(TwoSiloCacheUpdateFixture fixture)
    : IClassFixture<TwoSiloCacheUpdateFixture>
{
    /// <summary>
    /// How long the subscriber may wait while silo B lingers in its stop. Far inside the 30 s bound
    /// the production samples ran out: the hand-off to the surviving silo takes well under a second
    /// in this cluster.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    [Fact(Timeout = 180_000)]
    public async Task ASubscribeThatReachesAnOwnerOnAStoppingSilo_IsAnsweredPromptly()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the owner and its subscriber must live on different silos");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();

        // 1. The node, created and ACTIVATED on silo B. MessageHubGrain is [PreferLocalPlacement],
        //    so a delivery routed by B's router activates the owner on B.
        var ns = $"drain-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Doc", ns)
                {
                    Name = "Owned by the silo that is about to leave",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Emit("the owner answers a ping on silo B — the precondition for the rest", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the owner must be activated on silo B, the silo that stops — otherwise this test measures nothing");
        hubA.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().BeNull(
            "and not on silo A, where the subscriber lives");

        // Silo A has talked to the owner before — as every production caller of a busy node has — so
        // its directory cache points at silo B. That is what sends a delivery back to the leaving
        // silo after the owner's activation there is gone, where a plain deactivation would be
        // re-placed LOCALLY (the silo is still Active) instead of on a survivor.
        var accessA = siloA.GetRequiredService<AccessService>();
        await accessA.RunAsSystem(() => hubA.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Emit("the owner on silo B answers silo A before the stop", ct);
        hubA.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().BeNull(
            "silo A reached the existing activation on silo B rather than making its own");

        // 2. Silo B begins to stop, and lingers. Its grains are still active, so the directory still
        //    resolves the owner's address to B.
        siloB.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        // 3. A COLD subscriber on silo A — the shape of MeshNodeStreamHandle's initial-state wait in
        //    every production sample. Materialized and replayed, so neither arm is missed.
        var cache = siloA.GetRequiredService<IMeshNodeStreamCache>();
        var outcome = accessA.RunAsSystem(() => cache.GetStream(path, hubA.JsonSerializerOptions))
            .Take(1)
            .Materialize()
            .Replay();
        using var connected = outcome.Connect();

        try
        {
            // 4. Asserted while silo B is STILL LINGERING in its stop.
            var answer = await outcome.Should().Within(Prompt).Emit(
                "a subscriber whose owner lives on a stopping silo must be answered — by the owner, "
                + "by the next activation, or with a retryable failure — never left waiting out its "
                + "own budget with nothing said (#5256)", ct);
            answer.Kind.Should().Be(NotificationKind.OnNext,
                "the owner's address is handed off to the surviving silo, so the node is readable — "
                + $"got {answer.Kind}: {answer.Exception?.Message}");
            answer.Value!.Path.Should().Be(path);
            hubA.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
                "the answer came from a new activation on silo A, the caller's silo — the address was "
                + "handed off rather than served from the leaving one");
        }
        finally
        {
            await cluster.StopSiloAsync(cluster.Silos[1]);
        }
    }
}
