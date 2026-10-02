using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>Case 6, the production topology</b> (#5011). In the fleet watch the WRITER and the HOLDER are
/// different replicas: one replica's heartbeat writes <c>Ops/Status/*</c>, every other replica holds
/// the stream. So three silos: the owner on silo 1, the held read on silo 0, the writer on silo 2.
/// Silo 1 is killed; the owner re-activates on a survivor; silo 2 writes; silo 0's ALREADY-HELD read
/// must deliver.
///
/// <para>The two cases differ only in the holder's invalidation feed. In the second, silo 0's change
/// feed is HELD across the roll (<see cref="Testing.FaultInjection.FaultInjectingStorageAdapter.HoldChangeFeed"/>
/// — local commits and relayed ones alike: a LISTEN connection that died with the pod), so whatever
/// the held read delivers must come through the sync stream it holds on the owner. That separates
/// the two channels a held read could be fed by, which is what the #5011 freeze left open.</para>
/// </summary>
public abstract class AHeldReadOnAThirdSilo(FaultInjectionCluster mesh)
{
    /// <summary>Whether the holder's invalidation feed is withheld across the roll.</summary>
    protected abstract bool WithholdInvalidation { get; }

    [Fact(Timeout = 180_000)]
    public async Task TheOwnerSiloIsKilled_AndAWriteFromAThirdSilo_ReachesTheHeldRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "fleet-watch", ct);

        var withheld = WithholdInvalidation ? mesh.Storage(0).HoldChangeFeed() : null;
        try
        {
            await mesh.Kill(1);
            await Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
                .Select(_ => new[] { 0, 2 }.Any(s =>
                    mesh.Hub(s).GetHostedHub(new Address(held.Path), HostedHubCreation.Never) is not null))
                .Where(active => active)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the owner re-activates on a survivor", ct);

            await held.WriteFrom(mesh, 2, "v2").Should().Within(TestTimeouts.Convergence)
                .Emit("the write from the third silo commits on the re-activated owner", ct);
            await held.Delivers("v2").Should().Within(TestTimeouts.Convergence)
                .Emit(WithholdInvalidation
                    ? "with no cross-process invalidation at all, the held read is fed by the sync stream "
                      + "it holds on the owner — it must follow the owner to its new activation (#5011)"
                    : "the held read on a third silo delivers a write made after its owner's silo died (#5011)", ct);
            if (withheld is not null)
                await withheld.Arrivals.Where(a => a.Contains(held.Path, StringComparison.OrdinalIgnoreCase))
                    .Should().Within(TestTimeouts.Convergence)
                    .Emit("the write's invalidation reached the holder's feed and was WITHHELD — otherwise "
                          + "this case separated nothing", ct);
        }
        finally
        {
            withheld?.Release();
        }
    }
}

/// <summary>The relay delivers, as a healthy LISTEN channel does.</summary>
public class AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillTest(AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillTest.Cluster mesh)
    : AHeldReadOnAThirdSilo(mesh), IClassFixture<AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillTest.Cluster>
{
    protected override bool WithholdInvalidation => false;

    /// <summary>Three silos.</summary>
    public class Cluster : FaultInjectionCluster
    {
        protected override int SiloCount => 3;
    }
}

/// <summary>The holder's invalidation feed is withheld across the roll: only the held sync stream can deliver.</summary>
public class AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillWithoutInvalidationTest(
    AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillWithoutInvalidationTest.Cluster mesh)
    : AHeldReadOnAThirdSilo(mesh), IClassFixture<AHeldReadOnAThirdSiloSurvivesTheOwnerSiloKillWithoutInvalidationTest.Cluster>
{
    protected override bool WithholdInvalidation => true;

    /// <summary>Three silos.</summary>
    public class Cluster : FaultInjectionCluster
    {
        protected override int SiloCount => 3;
    }
}

/// <summary>
/// 🚨 <b>What the harness found for #5011.</b> The ROLL, not the crash, in the production topology:
/// the owner's silo (1) LINGERS in its stop, a third silo (2) writes, and the owner's address is
/// handed off to silo 2 — the path is CHOSEN so the hand-off lands there
/// (<see cref="FaultInjectionCluster.HandOffTarget"/>), away from the holder (silo 0).
///
/// <para>A held read learns that its owner moved from exactly one channel: the change feed. Its
/// heartbeat is fire-and-forget (<c>JsonSynchronizationStream</c>: "the change-feed resubscribe is the
/// sole recycled-grain detector"), and the lingering owner's goodbye rides the refused router. So:</para>
/// <list type="bullet">
///   <item>with the cross-process notifications flowing (PostgreSQL LISTEN healthy), the held read
///     follows the owner and delivers the write;</item>
///   <item>with the holder's feed withheld, the held read FREEZES — no value, no error, no end — for as
///     long as the feed is withheld, and delivers the moment it is released.</item>
/// </list>
/// <para>That second behaviour is the #5011 symptom exactly ("reads as holding while it holds
/// nothing"): a reader's liveness across a roll is only as good as the LISTEN channel of the process
/// that holds it. The second case PINS it (so a fix — a heartbeat that detects an owner that no longer
/// knows the subscriber — flips it deliberately). Found while the hand-off target was left to chance:
/// the same case passed alone and froze in the suite, because the target is a per-process string hash.</para>
/// </summary>
public abstract class AHeldReadWhoseOwnerIsHandedOffToAThirdSilo(FaultInjectionCluster mesh)
{
    /// <summary>Whether the holder's change feed is withheld across the hand-off.</summary>
    protected abstract bool WithholdInvalidation { get; }

    /// <summary>Runs the roll; the variant decides what the held read must do.</summary>
    private protected async Task<(HeldRead Held, Testing.FaultInjection.FaultSwitch? Withheld)> RollAndWrite(
        System.Threading.CancellationToken ct, Func<HeldRead, Testing.FaultInjection.FaultSwitch?, Task> whileLingering)
    {
        var held = await HeldRead.Arrange(mesh, owner: 1, holder: 0, "fleet-watch-handoff", ct,
            choosePath: p => mesh.HandOffTarget(p, leaving: 1, active: [0, 1, 2]) == 2);
        var withheld = WithholdInvalidation ? mesh.Storage(0).HoldChangeFeed() : null;
        try
        {
            await using (mesh.Linger(1))
            {
                await held.WriteFrom(mesh, 2, "v2").Should().Within(TestTimeouts.Convergence)
                    .Emit("the write from the third silo is re-driven past the lingering owner (#5873)", ct);
                mesh.Hub(2).GetHostedHub(new Address(held.Path), HostedHubCreation.Never).Should().NotBeNull(
                    "precondition: the address was handed off to silo 2, away from the holder — otherwise the "
                    + "owner's commits are local to the holder and this case separates nothing");
                await whileLingering(held, withheld);
            }
        }
        finally
        {
            withheld?.Release();
        }
        return (held, withheld);
    }
}

/// <summary>The healthy production shape: notifications flow, and the held read follows the hand-off.</summary>
public class AHeldReadFollowsItsOwnersHandOffTest(AHeldReadFollowsItsOwnersHandOffTest.Cluster mesh)
    : AHeldReadWhoseOwnerIsHandedOffToAThirdSilo(mesh), IClassFixture<AHeldReadFollowsItsOwnersHandOffTest.Cluster>
{
    protected override bool WithholdInvalidation => false;

    [Fact(Timeout = 180_000)]
    public async Task TheOwnerIsHandedOffAwayFromTheHolder_AndTheHeldReadDeliversTheWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var (held, _) = await RollAndWrite(ct, (h, _) =>
            h.Delivers("v2").Should().Within(TestTimeouts.Convergence)
                .Emit("with the cross-process notification delivered, the held read follows its owner's "
                      + "hand-off and delivers the write (#5011)", ct));
        held.Dispose();
    }

    /// <summary>Three silos.</summary>
    public class Cluster : FaultInjectionCluster
    {
        protected override int SiloCount => 3;
    }
}

/// <summary>The #5011 shape: the holder's notifications are lost, and the held read freezes until they are not.</summary>
public class AHeldReadFreezesWhenItsOwnersHandOffIsNotNotifiedTest(AHeldReadFreezesWhenItsOwnersHandOffIsNotNotifiedTest.Cluster mesh)
    : AHeldReadWhoseOwnerIsHandedOffToAThirdSilo(mesh), IClassFixture<AHeldReadFreezesWhenItsOwnersHandOffIsNotNotifiedTest.Cluster>
{
    /// <summary>Five heartbeats: long enough that a heartbeat-driven recovery would have happened.</summary>
    private static readonly TimeSpan Frozen = TimeSpan.FromSeconds(5);

    protected override bool WithholdInvalidation => true;

    [Fact(Timeout = 180_000)]
    public async Task WithoutTheChangeFeed_TheHeldReadFreezesSilently_AndRecoversOnlyWhenItArrives()
    {
        var ct = TestContext.Current.CancellationToken;
        var (held, withheld) = await RollAndWrite(ct, async (h, feed) =>
        {
            await h.Delivers("v2").Should().NotEmit(Frozen,
                "PINNED GAP (#5011): the heartbeat is fire-and-forget and the owner's goodbye cannot leave a "
                + "lingering host, so with its change feed withheld the held read has NO channel that tells it "
                + "the owner moved — it neither delivers nor ends", ct);
            feed.Should().NotBeNull("the freeze variant withholds the holder's feed");
            await (feed?.Arrivals ?? Observable.Empty<string>()).Where(a => a.Contains(h.Path, StringComparison.OrdinalIgnoreCase))
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the write's notification reached the holder's feed and is being withheld — otherwise "
                      + "the freeze is not attributable to it", ct);
        });
        using (held)
            await held.Delivers("v2").Should().Within(TestTimeouts.Convergence)
                .Emit("released, the one withheld notification is enough: the held read re-subscribes and "
                      + "delivers — the change feed is the sole channel that recovers it", ct);
        withheld.Should().NotBeNull("the freeze variant withholds the holder's feed");
        withheld?.IsClosed.Should().BeFalse("the feed hold was released in the fixture's finally");
    }

    /// <summary>Three silos.</summary>
    public class Cluster : FaultInjectionCluster
    {
        protected override int SiloCount => 3;
    }
}
