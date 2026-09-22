using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro + regression guard for root R2 of <c>Doc/Architecture/DisposedScopeAndDyingHubs</c>:
/// a hosted hub that has reached <see cref="MessageHubRunLevel.ShutDown"/> being HANDED OUT by its
/// parent's registry as the live hub for its address (#5136).
///
/// <para><b>The window.</b> A hub at <c>ShutDown</c> can serve nothing: its intake refuses every
/// delivery and a direct <c>Observe(...)</c> faults synchronously with
/// <see cref="ObjectDisposedException"/>. It leaves its parent's registry only when its own removal
/// registrant runs — several statements INTO the ShutDown phase, after <c>CancelCallbacks</c> and the
/// reactive dispose actions. A teardown that wedges anywhere in between never gets there, and
/// <see cref="HostedHubsCollection.GetHubWithOutcome"/> kept answering the corpse as
/// <see cref="HostedHubOutcome.Available"/> for as long as the wedge lasted. Production measured ONE
/// such hub handed to four callers over 55 minutes on a pod that was otherwise serving, each faulting
/// out of an HTTP endpoint as a 500.</para>
///
/// <para><b>Why this test is deterministic.</b> The wedge is REPRODUCED, not waited for: the hub
/// registers a reactive dispose action that parks the ShutDown turn — those actions are invoked
/// synchronously inside <c>DisposeImpl</c>, before the registrant walk that carries the removal — so
/// the hub sits at exactly <c>RunLevel=ShutDown, still registered</c> for as long as the test wants.
/// The park is the SUBJECT, so it is a volatile <c>int</c> polled under a bounded
/// <c>SpinWait.SpinUntil</c>, released in a <c>finally</c> so a failing assertion cannot strand the
/// pump thread; the park's engagement is a producer→test <see cref="AsyncSubject{T}"/>, and it is
/// ASSERTED, so a park that never engaged cannot read as a pass. (The hub's own stall detector
/// reports the wedge at Error while it lasts — <c>DisposalShutDownPhaseBlocked</c>, the #4883
/// verdict — which is the correct reading of what this test manufactures.)</para>
///
/// <para><b>Control, measured.</b> With the registry hit returned unconditionally (the previous
/// first line of <c>GetHubWithOutcome</c>), <see cref="AHubParkedAtShutDown_IsNotHandedOut_ASuccessorIs"/>
/// fails on <c>successor.Should().NotBeSameAs(first)</c> — the lookup hands back the corpse; with
/// the corpse retired it passes. <see cref="TheOwnerStillJoinsARetiredHub"/> is the half that keeps
/// the first half safe: a retired hub's Autofac scope is a child of its owner's, so an owner that
/// finished its own teardown ahead of the corpse would close that scope under a hub still resolving
/// from it — the R1 straggler class, manufactured locally.</para>
/// </summary>
public class AHubAtShutDownIsNotHandedOutTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Written by the test to release the parked ShutDown turn; polled by the park.</summary>
    private int release;

    /// <summary>Completed by the parked turn the instant it is parked — the producer→test signal.</summary>
    private readonly AsyncSubject<Unit> parked = new();

    /// <summary>How long the park held, and what it saw when it released — printed, never asserted.</summary>
    private volatile string parkDiagnostics = "(park never engaged)";

    /// <summary>
    /// Parks the hub's ShutDown turn inside a reactive dispose action, which <c>DisposeImpl</c>
    /// invokes BEFORE the registrant walk that would take the hub out of its parent's registry.
    /// </summary>
    private MessageHubConfiguration ParkOnShutDown(MessageHubConfiguration c) =>
        c.WithInitialization(hub => hub.RegisterForDisposal(_ =>
        {
            parked.OnNext(Unit.Default);
            parked.OnCompleted();
            var sw = Stopwatch.StartNew();
            try
            {
                // Bounded, and the bound is a DIAGNOSTIC rather than a budget: the test releases
                // it in its own finally, long before this elapses.
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
            }
            finally
            {
                parkDiagnostics = $"ShutDown turn parked {sw.ElapsedMilliseconds}ms; released at RunLevel={hub.RunLevel}";
            }
            return Observable.Return(Unit.Default);
        }));

    [HubFact]
    public async Task AHubParkedAtShutDown_IsNotHandedOut_ASuccessorIs()
    {
        var host = GetHost();
        var collection = host.ServiceProvider.GetRequiredService<HostedHubsCollection>();
        var address = new Address("child", "parked-at-shutdown-" + Guid.NewGuid().AsString());

        var first = host.GetHostedHub(address, ParkOnShutDown);
        host.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(first,
            "a live hub is registered under its address from the moment it is built");

        try
        {
            // 1. Tear it down directly and hold it at ShutDown — the phase the corpse was measured
            //    in, with its registry removal still ahead of it.
            first.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the repro only means anything if the ShutDown turn was actually parked");
            first.RunLevel.Should().Be(MessageHubRunLevel.ShutDown,
                "the park sits inside the ShutDown phase — after the RunLevel flip, before the registrant walk");
            host.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(first,
                "PRECONDITION: the corpse is still registered — its removal registrant has not run, "
                + "which is exactly the state #5136 measured. Without this the lookup below tests nothing");

            // 2. The lookup a caller with new work makes. This is the #5136 call: a hub it can post
            //    to, from a registry that holds a corpse.
            var successor = host.GetHostedHub(address, c => c);

            successor.Should().NotBeSameAs(first,
                "a hub at ShutDown can serve nothing — its intake refuses every delivery and Observe faults "
                + "synchronously — so answering it as Available hands the caller a guaranteed "
                + "ObjectDisposedException for as long as the teardown stays wedged (#5136)");
            successor.IsDisposing.Should().BeFalse("the successor is a fresh activation, not the corpse");
            await successor.RunLevelChanged.Where(l => l >= MessageHubRunLevel.Started).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit("the successor starts like any hosted hub");
            host.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(successor,
                "the successor is registered under the address the corpse was retired from");
            host.GetHostedHub(address, c => c).Should().BeSameAs(successor,
                "a second lookup finds the successor, not another one — the registry holds one live hub per address");

            // 3. The corpse is still OWNED: retired from the registry, not dropped from the collection.
            collection.Hubs.Should().Contain(h => ReferenceEquals(h, first),
                "a retired hub stays in the collection until it is Dead — the owner's join and its "
                + "diagnostics must still see it");
            first.RunLevel.Should().Be(MessageHubRunLevel.ShutDown, "nothing about retiring it moved its teardown");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        // 4. Let the corpse finish. Its own late removal is value-matched, so it must find the
        //    successor and leave it alone.
        await first.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the released ShutDown turn runs the corpse to Dead");
        Output.WriteLine($"PARK: {parkDiagnostics}");
        first.RunLevel.Should().Be(MessageHubRunLevel.Dead);

        var survivor = host.GetHostedHub(address, HostedHubCreation.Never);
        survivor.Should().NotBeNull("the successor must survive its predecessor's teardown");
        survivor.Should().BeSameAs(host.GetHostedHub(address, c => c),
            "the corpse's late removal is value-matched and must not have evicted the successor");
        survivor!.IsDisposing.Should().BeFalse("the predecessor's teardown must not have touched the successor");
        collection.Hubs.Count(h => h.Address.Equals(address)).Should().Be(1,
            "once the corpse is Dead it leaves the collection, and exactly one hub remains for the address");
    }

    /// <summary>
    /// The half that keeps retirement safe: the owner's teardown still WAITS for a retired hub.
    /// The corpse is parked at ShutDown, a successor is minted, and then the OWNER is disposed —
    /// its <c>DisposalCompleted</c> must not fire while the corpse is parked, and must fire once the
    /// corpse is released. Without the join the owner would advance to its own ShutDown, close its
    /// container, and the corpse's child scope with it, while the corpse was still inside its
    /// registrant walk.
    /// </summary>
    [HubFact]
    public async Task TheOwnerStillJoinsARetiredHub()
    {
        var host = GetHost();
        var address = new Address("child", "retired-and-joined-" + Guid.NewGuid().AsString());

        var first = host.GetHostedHub(address, ParkOnShutDown);
        IMessageHub? successor = null;
        try
        {
            first.Dispose();
            await parked.Should().Within(TestTimeouts.Convergence).Emit(
                "the repro only means anything if the ShutDown turn was actually parked");
            successor = host.GetHostedHub(address, c => c);
            successor.Should().NotBeSameAs(first, "PRECONDITION: the corpse was retired and a successor minted");

            // The OWNER goes down while its retired child is still parked inside ShutDown.
            host.Dispose();
            await host.RunLevelChanged.Where(l => l >= MessageHubRunLevel.DisposeHostedHubs).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit("the owner reaches its child-disposal phase");
            await host.DisposalCompleted.Should().NotEmit(TestTimeouts.Quick,
                "the owner must not finish its teardown while a hub it retired is still tearing down — "
                + "its container is that hub's parent scope, and closing it would strand the corpse's "
                + "registrant walk on a disposed LifetimeScope (the R1 straggler class)");
            host.RunLevel.Should().Be(MessageHubRunLevel.DisposeHostedHubs,
                "the owner is parked in the join, on the retired child, not somewhere else");
            successor.IsDisposing.Should().BeTrue(
                "the successor was in the registry, so the owner's cascade disposed it like any child");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await host.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "once the retired child is Dead the join settles and the owner finishes");
        Output.WriteLine($"PARK: {parkDiagnostics}");
        first.RunLevel.Should().Be(MessageHubRunLevel.Dead, "the retired hub finished before its owner did");
        successor!.RunLevel.Should().Be(MessageHubRunLevel.Dead, "the successor went down with the owner");
    }
}
