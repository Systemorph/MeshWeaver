using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Case 1 — a write during a pod roll</b> (MeshWeaver#5873; issues #5011, MeshWeaver.Plugins#2403).
///
/// <para>The owner lives on silo 1; silo 0 holds its stream. Silo 1 is told to stop and LINGERS
/// (<see cref="FaultInjectionCluster.Linger"/>): its grains refuse every delivery as
/// <c>ShuttingDown</c> and hand their address off. Silo 0 writes the node. The write must be RE-DRIVEN
/// against the next activation — not lost — and the held read must deliver it. The cross-silo
/// invalidation is the relay's, as in production; nothing is published by hand.</para>
///
/// <para><b>Negative control:</b> with #5873's two <c>ShuttingDown</c> arms in
/// <c>MeshNodeStreamHandle.UpdateRemote</c> reverted, the write faults in about a second with
/// <c>MeshNode Unknown … Rejecting now</c>. See Doc/Architecture/FaultInjectionHarness.</para>
/// </summary>
public class AWriteDuringAPodRollIsReDrivenTest(AWriteDuringAPodRollIsReDrivenTest.Cluster mesh)
    : IClassFixture<AWriteDuringAPodRollIsReDrivenTest.Cluster>
{
    [Fact(Timeout = 180_000)]
    public async Task AWriteToAnOwnerWhoseSiloIsStopping_IsReDriven_AndTheHeldReadDeliversIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "roll-write", ct);

        await using (mesh.Linger(1))
        {
            await held.Write("v2").Should().Within(TestTimeouts.Convergence)
                .Emit("a write that meets its owner on a stopping silo is re-driven against the next "
                      + "activation, never lost (#5873)", ct);
            await held.Delivers("v2").Should().Within(TestTimeouts.Convergence)
                .Emit("the read held across the roll delivers the re-driven write", ct);
        }
    }

    /// <summary>Its own cluster: the case stops a silo.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>
/// <b>Case 6 — the fleet watch freeze</b> (#5011): a reader's subscription must not end silently when
/// the silo that owns its source rolls. Killed and drained here (lingering is case 1 above, and the
/// three-silo topology is in <c>FleetWatchFreezeTests</c>); in each the ALREADY-HELD read must deliver
/// the next write. With case 1 they replace the
/// <c>AHeldReadKeepsDelivering…</c> trio that published the cross-silo invalidation by hand; here the
/// relay delivers it, as PostgreSQL LISTEN does.
/// </summary>
public abstract class AHeldReadSurvivesItsSourceSiloLeaving(FaultInjectionCluster mesh)
{
    /// <summary>How silo 1 (the owner's) leaves.</summary>
    protected abstract Task Leave(FaultInjectionCluster cluster);

    [Fact(Timeout = 180_000)]
    public async Task AWriteAfterTheOwnerSiloLeft_ReachesTheHeldRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "roll-read", ct);

        await Leave(mesh);

        await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .Select(_ => mesh.Hub(0).GetHostedHub(new Address(held.Path), HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the held stream re-activates the owner on the survivor", ct);
        await held.Write("v2").Should().Within(TestTimeouts.Convergence)
            .Emit("the write after the move commits on the re-activated owner", ct);
        await held.Delivers("v2").Should().Within(TestTimeouts.Convergence)
            .Emit("a read HELD across its owner's silo leaving delivers the next write (#5011)", ct);
    }
}

/// <summary>The owner's silo is killed (SIGKILL / SIGSEGV).</summary>
public class AHeldReadSurvivesItsSourceSiloBeingKilledTest(AHeldReadSurvivesItsSourceSiloBeingKilledTest.Cluster mesh)
    : AHeldReadSurvivesItsSourceSiloLeaving(mesh), IClassFixture<AHeldReadSurvivesItsSourceSiloBeingKilledTest.Cluster>
{
    protected override Task Leave(FaultInjectionCluster cluster) => cluster.Kill(1);

    /// <summary>Its own cluster.</summary>
    public class Cluster : FaultInjectionCluster;
}

/// <summary>The owner's silo is drained (a rolling restart).</summary>
public class AHeldReadSurvivesItsSourceSiloDrainingTest(AHeldReadSurvivesItsSourceSiloDrainingTest.Cluster mesh)
    : AHeldReadSurvivesItsSourceSiloLeaving(mesh), IClassFixture<AHeldReadSurvivesItsSourceSiloDrainingTest.Cluster>
{
    protected override Task Leave(FaultInjectionCluster cluster) => cluster.Drain(1);

    /// <summary>Its own cluster.</summary>
    public class Cluster : FaultInjectionCluster;
}
