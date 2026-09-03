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
/// 🚨 <b>A BuildupAction never STARTS after its hub has begun shutting down.</b>
///
/// <para>The init turn (<c>InitializeHubRequest</c>) is queued at <c>Build</c> and runs whenever the
/// action block reaches it. That can be after the hub's own <c>Dispose()</c>: a transient probe is
/// created and disposed in one breath (<c>ContentTypeRegistration.ProbeRegister</c> at boot, the
/// schema probes), and an ancestor's <c>Dispose()</c> freezes a whole subtree before any descendant
/// has initialised. Every <c>WithInitialization</c> action is a piece of the per-node control plane
/// — a watcher over the own node, an eagerly created child hub, a ticker — and on a hub that is
/// already leaving each one is born dead and faults on the way out. The one this pins is the child
/// creation: <c>HostedHubsCollection</c> refuses it with a Warning (<c>Rejecting hosted hub creation
/// … during disposal</c>). Measured on the Thread NodeType's boot-time registration probe: 159 of
/// 643 test logs of one CI run carried that Warning (MeshWeaver CD 33619142646), and the one test
/// asserting a fault-free probe teardown red whenever the late creation landed inside its window
/// (<c>ProbeHubCostTest.ValidateContentWithSchema_OnInvalidContent_BuildsOneProbeNotTwo</c>).</para>
///
/// <para>The rule is the init-turn form of #3026/#3072 ("a watcher stops at the first instant of
/// teardown"): at each action boundary, <c>HandleInitialize</c> checks <c>IsShuttingDown</c> and
/// skips the remaining actions — the gate still opens so the disposal state machine flows, and
/// nothing is installed. See <c>Doc/Architecture/HubDisposalModel</c> → "The first instant of
/// teardown".</para>
///
/// <para>No hand-woven gate: the action → test signal is an <c>AsyncSubject</c> the action
/// completes; the test → parked action release is a volatile flag polled under a bounded
/// <c>SpinUntil</c>, written in <c>finally</c>. The park IS the subject — it makes "teardown began
/// mid-initialization" a certainty instead of a race.</para>
/// </summary>
public class InitializationStopsAtTeardownStartTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public async Task DisposeDuringInitialization_SkipsTheRemainingBuildupActions()
    {
        var parkEntered = new AsyncSubject<Unit>();
        var release = 0;
        var parkTimedOut = 0;
        var laterActionRan = 0;
        var client = GetClient();
        var hub = client.ServiceProvider.CreateMessageHub(
            new Address("init-at-teardown", "1"),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitialization(_ => Observable.Defer(() =>
                {
                    parkEntered.OnNext(Unit.Default);
                    parkEntered.OnCompleted();
                    // Parks the init turn on the action block until the test has called Dispose().
                    // A park that ends on its BUDGET rather than on the release is recorded, so the
                    // later action running for that reason is reported as such — not as the rule
                    // under test failing.
                    if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence))
                        Interlocked.Exchange(ref parkTimedOut, 1);
                    return Observable.Return(Unit.Default);
                }))
                .WithInitialization(h => Observable.Defer(() =>
                {
                    Interlocked.Exchange(ref laterActionRan, 1);
                    // The shape of every per-node control plane: an eagerly created child hub
                    // (ThreadExecution.InstallExecutionHub's _Exec). On a hub that is leaving,
                    // HostedHubsCollection refuses it with a Warning — the fault this pins away.
                    h.GetHostedHub(new Address("init-at-teardown-child", "1"), cfg => cfg, HostedHubCreation.Always);
                    return Observable.Return(Unit.Default);
                })));

        try
        {
            await parkEntered.Should().Within(TestTimeouts.Convergence)
                .Emit("the first BuildupAction must be running, or the park proves nothing");
            Volatile.Read(ref laterActionRan).Should().Be(0,
                "BuildupActions run in registration order; the second cannot start while the first is parked");
            hub.Dispose();
            hub.IsShuttingDown.Should().BeTrue("Dispose() begins this hub's teardown synchronously");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        // The ShutdownRequest that Dispose() posted is queued BEHIND the init turn, so the disposal
        // completing is the positive signal that the init turn has ended — the assertion below cannot
        // fire before the action it tests would have run.
        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("a hub disposed mid-initialization must still finish disposing");
        Volatile.Read(ref parkTimedOut).Should().Be(0,
            "the park must end on the release the test writes in its finally, never on its budget — "
            + "a timed-out park would let the later action run for a reason unrelated to the rule under test");
        Volatile.Read(ref laterActionRan).Should().Be(0,
            "a BuildupAction must never start after the hub began shutting down: the control plane it "
            + "would install is born dead and faults on the way out (a child creation refused with a "
            + "Warning, a watcher errored by the teardown)");
    }

    /// <summary>
    /// The control arm: on a hub that is NOT shutting down, every action runs and the child exists —
    /// so the test above cannot pass by an initialization that installs nothing at all.
    /// </summary>
    [Fact]
    public async Task LiveHub_RunsEveryBuildupAction_AndCreatesItsChild()
    {
        var laterActionDone = new AsyncSubject<Unit>();
        var childAddress = new Address("init-live-child", "1");
        var client = GetClient();
        var hub = client.ServiceProvider.CreateMessageHub(
            new Address("init-live", "1"),
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitialization(_ => Observable.Return(Unit.Default))
                .WithInitialization(h => Observable.Defer(() =>
                {
                    h.GetHostedHub(childAddress, cfg => cfg, HostedHubCreation.Always);
                    laterActionDone.OnNext(Unit.Default);
                    laterActionDone.OnCompleted();
                    return Observable.Return(Unit.Default);
                })));

        await laterActionDone.Should().Within(TestTimeouts.Convergence)
            .Emit("on a live hub the second BuildupAction runs");
        hub.GetHostedHub(childAddress, HostedHubCreation.Never).Should().NotBeNull(
            "the child created by the init turn exists on a live hub");

        hub.Dispose();
        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit("the live hub disposes");
    }
}
