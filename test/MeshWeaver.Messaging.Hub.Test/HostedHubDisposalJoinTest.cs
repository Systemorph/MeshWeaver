using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// #1317 — <c>HostedHubsCollection</c> must JOIN its children's disposal, not pre-empt it.
///
/// <para><b>The inversion.</b> The collection capped its wait at a flat 5 s literal. What it was
/// waiting on answers from its own terminal state: every hosted hub is a <c>MessageHub</c>, and it
/// signals <c>DisposalCompleted</c> as the last act of its own phased teardown once the work it
/// accepted has drained — so a child's answer is guaranteed to come, just not within 5 s. The cap
/// therefore fired BEFORE the mechanism that would have produced a clean answer, in exactly the
/// busy case it was written for. Nesting makes it worse without any wedge at all:
/// a child's own disposal is quiesce (2 s default) plus its own hosted-subtree wait, so a
/// legitimately busy two-level tree exceeds a flat 5 s while every individual step is inside its
/// own budget.</para>
///
/// <para><b>What that costs.</b> When the cap fires the collection signals done anyway, the owner
/// advances to ShutDown and tears the DI container down — while children are still mid-disposal,
/// resolving services from it. That is the post-teardown straggler class the collection's own
/// comments describe (#613), and it is the "leaked hub state" the incident named. Bounded, not
/// permanent — the children's watchdogs still collect them — but against a container that is gone.</para>
///
/// <para><b>The assertion is the collection's OWN documented contract</b>, extended past the
/// literal: <c>InflightHubCreationDrainTest</c> already pins "disposal FINISHES the requests it
/// started" and proves it holds for 1 s. This proves it must hold for longer than the cap, because
/// nothing about an in-flight construction promises to finish inside an arbitrary 5 s.</para>
/// </summary>
public class HostedHubDisposalJoinTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Sits between the removed 5 s cap and <c>MessageHub</c>'s 8 s disposal stall budget — the only
    /// band in which the two bounds disagree, which is precisely the defect's footprint. Long
    /// enough that the cap has certainly fired (it is a fixed timer, so this margin does not
    /// shrink under CI load), short enough that the owner's stall detector has certainly not looked.
    /// </summary>
    private static readonly TimeSpan PastTheOldCap = TimeSpan.FromSeconds(6.5);

    /// <summary>
    /// A hosted-hub construction held mid-flight is a child the owner is contractually required to
    /// finish. Before the fix the owner abandoned it at the 5 s cap and reported itself fully
    /// disposed; after it, the join waits for the answer it is joining.
    /// </summary>
    [HubFact]
    public async Task DisposalWaitsForAnInflightConstruction_PastTheOldFiveSecondCap()
    {
        var client = GetClient();

        // 🚨 No hand-woven gate: construction → test is an AsyncSubject the producer completes;
        // the release travels INTO the thread parked inside Build, so it is a volatile flag polled
        // under a bounded SpinUntil and written in the `finally` below.
        var constructionEntered = new AsyncSubject<Unit>();
        var releaseConstruction = 0;

        // Same latch shape as InflightHubCreationDrainTest: the WithInitialization hook runs
        // synchronously inside Build, so this thread parks INSIDE construction — where the routed
        // straggler emissions of the #613 class sit when teardown begins. The flag, not a timer,
        // is what holds the window open, so nothing here samples a race.
        var creation = Task.Run(() => client.GetHostedHub(
            new Address("slow-inflight", "1"),
            c => c.WithInitialization(_ =>
            {
                constructionEntered.OnNext(Unit.Default);
                constructionEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseConstruction) == 1, TimeSpan.FromSeconds(30));
            }),
            HostedHubCreation.Always));

        try
        {
            await constructionEntered.Should().Within(10.Seconds()).Emit(
                "the hosted-hub construction must have started before disposal begins");

            var disposalCompleted = client.DisposalCompleted.Take(1).Await();
            client.Dispose();

            // Sanctioned negative wait: the finding IS that nothing happened. There is no positive
            // signal to filter for — "the owner did not declare itself done" has no emission.
            var winner = await Task.WhenAny(disposalCompleted, Task.Delay(PastTheOldCap));

            winner.Should().NotBe(disposalCompleted,
                "the owner declared its hosted hubs disposed while a construction it started was "
                + "still running — so it went on to tear down the container underneath that Build. "
                + "A join must not out-run the answers it is joining: the child's completion comes "
                + "from its own teardown once the work it started has finished, and a second, "
                + "SHORTER budget layered above it can only ever pre-empt that");

            Volatile.Write(ref releaseConstruction, 1);

            await disposalCompleted.WaitAsync(TimeSpan.FromSeconds(15));

            var lateHub = await creation.WaitAsync(TimeSpan.FromSeconds(10));
            lateHub.Should().NotBeNull(
                "the creation was started before disposal, so the contract is to finish it, not refuse it");
            await lateHub!.DisposalCompleted.Take(1).Await().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Volatile.Write(ref releaseConstruction, 1);
        }
    }

    /// <summary>
    /// The other direction, and the one that keeps the fix from being "wait forever": an ordinary
    /// teardown with nothing wedged must still complete PROMPTLY. Removing a cap is only correct if
    /// the join was never what made the common case fast — it was not; the children answer at once.
    /// </summary>
    [HubFact]
    public async Task OrdinaryDisposalStillCompletesPromptly()
    {
        var client = GetClient();
        client.GetHostedHub(new Address("prompt", "1"), c => c, HostedHubCreation.Always)
            .Should().NotBeNull();

        var disposalCompleted = client.DisposalCompleted.Take(1).Await();
        client.Dispose();

        await disposalCompleted.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
