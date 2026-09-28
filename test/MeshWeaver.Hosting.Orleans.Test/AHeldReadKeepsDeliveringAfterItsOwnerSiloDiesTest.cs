using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issues #5011 and MeshWeaver.Plugins#2403: a HELD read must keep DELIVERING after the silo that
/// owned the node dies — not merely re-activate the owner.
///
/// <para><see cref="AKilledOwnerSiloIsReactivatedByItsHoldersTest"/> proves the held stream's
/// heartbeat re-places the owner on the survivor. That is half the contract. The fleet watch's
/// heartbeat and the running-action resumer both HOLD a node's stream for the life of the process
/// and act on every later version: a reader that is attached to nothing after the owner moved
/// reads as holding while it holds nothing (09-27: the replica's heartbeat froze at 16:19:23Z in
/// the middle of a restart roll while the writer, the stored node and a fresh read were current).</para>
///
/// <para><b>The contract.</b> After the owner's silo is killed and the owner is re-activated on the
/// survivor, a write to the node reaches the ALREADY-HELD subscription.</para>
/// </summary>
public class AHeldReadKeepsDeliveringAfterItsOwnerSiloDiesTest(AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture fixture)
    : AHeldReadAcrossAnOwnerMove(fixture), IClassFixture<AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture>
{
    protected override Task Leave(TestCluster cluster, IServiceProvider siloB) => cluster.KillSiloAsync(cluster.Silos[1]);
}

/// <summary>
/// The ROLL shape: the owner's silo is told to stop (SIGTERM → <c>StopApplication</c>) and then
/// leaves gracefully — its grains deactivate, which is what a rolling restart of a portal does to
/// every per-node hub on the old pod.
/// </summary>
public class AHeldReadKeepsDeliveringAfterItsOwnerSiloDrainsTest(AHeldReadKeepsDeliveringAfterItsOwnerSiloDrainsTest.Fixture fixture)
    : AHeldReadAcrossAnOwnerMove(fixture), IClassFixture<AHeldReadKeepsDeliveringAfterItsOwnerSiloDrainsTest.Fixture>
{
    protected override async Task Leave(TestCluster cluster, IServiceProvider siloB)
    {
        siloB.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        await cluster.StopSiloAsync(cluster.Silos[1]);
    }

    /// <summary>Its own cluster: the test removes a silo.</summary>
    public class Fixture : AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture;
}

public abstract class AHeldReadAcrossAnOwnerMove(TwoSiloCacheUpdateFixture fixture)
{
    /// <summary>How silo B leaves the cluster.</summary>
    protected abstract Task Leave(TestCluster cluster, IServiceProvider siloB);

    private static readonly TimeSpan Resumes = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Delivers = TimeSpan.FromSeconds(20);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    [Fact(Timeout = 180_000)]
    public async Task AWriteAfterTheOwnerSiloLeft_ReachesTheHeldRead()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the owner and its holder must live on different silos");

        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();

        var ns = $"held-{Guid.NewGuid():N}";
        var path = $"{ns}/Status";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Status", ns)
                {
                    Name = "v1",
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the owner answers a ping on silo B — the precondition for the rest", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the owner must be activated on silo B, the silo that dies — otherwise this test measures nothing");

        // The heartbeat/resumer shape: ONE long-lived subscription on silo A, replayed so every
        // version it ever delivered can be asserted on.
        var accessA = siloA.GetRequiredService<AccessService>();
        var cache = siloA.GetRequiredService<IMeshNodeStreamCache>();
        var held = accessA.RunAsSystem(() => cache.GetStream(path, hubA.JsonSerializerOptions)).Replay();
        using var holding = held.Connect();
        await held.Where(n => n.Name == "v1").Should().Within(TestTimeouts.Convergence)
            .Emit("silo A's held stream receives the owner's node", ct);

        await Leave(cluster, siloB);

        await Observable.Interval(TimeSpan.FromMilliseconds(250))
            .StartWith(0L)
            .Select(_ => hubA.GetHostedHub(new Address(path), HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(Resumes)
            .Emit("the held stream re-activates the owner on the survivor (the half already covered)", ct);

        // The write the fleet watch makes every pass, and the one an adopted action makes when it
        // finishes: through the mesh, as the system.
        await accessA.RunAsSystem(() => cache.Update(path, n => n with { Name = "v2" }, hubA.JsonSerializerOptions))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the write after the move commits on the re-activated owner", ct);

        var terminal = held.Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext);
        await held.Where(n => n.Name == "v2").Amb(terminal.SelectMany(n =>
                Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"the held read ENDED ({n.Kind}: {n.Exception?.Message}) instead of delivering"))))
            .Should().Within(Delivers)
            .Emit("a read HELD across the owner's silo dying must deliver the next write — a holder "
                  + "attached to nothing reads as holding while it holds nothing (#5011, Plugins#2403)", ct);
    }
}

/// <summary>
/// The ROLL shape at its worst moment: the owner's silo has been told to stop and is still
/// LINGERING (its grains active, the directory still pointing at it) when the next write lands. The
/// cross-process invalidation the PostgreSQL LISTEN relay gives every replica in production is
/// published on the holder's silo explicitly, exactly as <c>StorageChangeFeedRelay</c> would.
/// </summary>
public class AHeldReadKeepsDeliveringWhileItsOwnerSiloLingersTest(AHeldReadKeepsDeliveringWhileItsOwnerSiloLingersTest.Fixture fixture)
    : IClassFixture<AHeldReadKeepsDeliveringWhileItsOwnerSiloLingersTest.Fixture>
{
    private static readonly TimeSpan Delivers = TimeSpan.FromSeconds(20);

    private static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    [Fact(Timeout = 180_000)]
    public async Task AWriteWhileTheOwnerSiloLingers_ReachesTheHeldRead()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        var siloA = SiloServices(cluster, 0);
        var siloB = SiloServices(cluster, 1);
        var hubA = siloA.GetRequiredService<IMessageHub>();
        var hubB = siloB.GetRequiredService<IMessageHub>();

        var ns = $"linger-{Guid.NewGuid():N}";
        var path = $"{ns}/Status";
        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode("Status", ns) { Name = "v1", NodeType = "Markdown", State = MeshNodeState.Active }))
            .FirstAsync().Await(ct);
        await accessB.RunAsSystem(() => hubB.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the owner answers a ping on silo B", ct);
        hubB.GetHostedHub(new Address(path), HostedHubCreation.Never).Should().NotBeNull(
            "the owner must live on silo B, the silo that stops");

        var accessA = siloA.GetRequiredService<AccessService>();
        var cache = siloA.GetRequiredService<IMeshNodeStreamCache>();
        var held = accessA.RunAsSystem(() => cache.GetStream(path, hubA.JsonSerializerOptions)).Replay();
        using var holding = held.Connect();
        await held.Where(n => n.Name == "v1").Should().Within(TestTimeouts.Convergence)
            .Emit("silo A's held stream receives the owner's node", ct);

        siloB.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        try
        {
            var written = await accessA.RunAsSystem(() =>
                    cache.Update(path, n => n with { Name = "v2" }, hubA.JsonSerializerOptions))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the write while silo B lingers commits — on B or on the next activation", ct);

            // What the PostgreSQL LISTEN relay does on every replica that did not make the write.
            siloA.GetRequiredService<MeshWeaver.Hosting.InProcessMeshChangeFeed>()
                .PublishLocal(MeshChangeEvent.Updated(written));

            var terminal = held.Materialize().Where(n => n.Kind != System.Reactive.NotificationKind.OnNext);
            await held.Where(n => n.Name == "v2").Amb(terminal.SelectMany(n =>
                    Observable.Throw<MeshNode>(new InvalidOperationException(
                        $"the held read ENDED ({n.Kind}: {n.Exception?.Message}) instead of delivering"))))
                .Should().Within(Delivers)
                .Emit("a read HELD while its owner's silo lingers in its stop must deliver the write "
                      + "(#5011, Plugins#2403)", ct);
        }
        finally
        {
            await cluster.StopSiloAsync(cluster.Silos[1]);
        }
    }

    /// <summary>Its own cluster: the test removes a silo.</summary>
    public class Fixture : AKilledOwnerSiloIsReactivatedByItsHoldersTest.Fixture;
}
