using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro + regression guard for a hosted hub being RESURRECTED in its parent's
/// registry after it had already disposed itself (#4741).
///
/// <para><b>The window.</b> A hosted hub is registered TWICE on the creation path: by
/// <see cref="HostedHubsCollection.Add"/> from inside <c>MessageHubConfiguration.Build</c> — the
/// first statement after the hub is constructed, before its synchronous buildup actions and before
/// <c>StartMessageProcessing</c> — and again by <see cref="HostedHubsCollection.GetHubWithOutcome"/>'s
/// creation <c>Lazy</c> once <c>Build</c> has returned. The first registration arms the removal. A
/// hub disposed in between — by a <see cref="HostedHubsCollection.HubAdded"/> subscriber, an
/// ancestor's cascade, a probe created and disposed in one breath — does not wait for <c>Build</c>:
/// its <c>ShutdownRequest</c> is drained the moment it is posted, bypasses every init gate, and takes
/// the hub to <see cref="MessageHubRunLevel.Dead"/> in milliseconds, FIRING ITS REMOVAL, while the
/// constructing thread is still inside <c>Build</c>. The Lazy's bare re-put then placed the corpse
/// back under its address with nothing left to take it out.</para>
///
/// <para><b>What that costs.</b> Every stream message and every user action routed to that address
/// afterwards found a registered hub in the parent-chain walk, was delivered into it and was
/// discarded there: no refusal, no drop warning, no line at all. That is the silent shape #4741
/// measured — a click on a reaped stream that produced NOTHING for the whole 36 s wait — and it was
/// reachable from any test that disposes the hub a <c>HubAdded</c> subscriber handed it.</para>
///
/// <para><b>Why this test is deterministic where a 32-run loop found it once.</b> The dispose is
/// issued from the FIRST <c>HubAdded</c> emission (the one <c>Add</c> raises), and the constructing
/// thread is then PARKED in the hub's own synchronous buildup action — which <c>Build</c> runs right
/// after <c>Add</c>, on that thread, outside any <c>Post</c> — until the pump has taken the hub all
/// the way to Dead. <c>Build</c> therefore returns to the creation Lazy holding a corpse, every
/// time. The park is the SUBJECT of the test, so it is a bounded <c>SpinWait.SpinUntil</c> over a
/// value another worker writes, never a gate; it is written in a <c>finally</c>-guarded shape and
/// its engagement is asserted, so a park that never happened cannot read as a pass. (Parking inside
/// <c>Post</c> instead — on the <c>InitializeHubRequest</c> that ends <c>Build</c> — was measured
/// and rejected: the teardown guard skips the post pipeline once the hub is past
/// <c>DisposeHostedHubs</c>, so that park raced the very shutdown it waited for.)</para>
///
/// <para><b>Control, measured.</b> With the Lazy's bare <c>messageHubs[a] = created.Hub</c> restored,
/// this test fails on the registry assertion — the address still resolves to a hub whose
/// <c>RunLevel</c> is Dead; with the removal re-armed through <c>Track</c> it passes.</para>
/// </summary>
public class AHubDisposedWhileBeingBuiltLeavesTheRegistryTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Set once the constructing thread was parked inside the hub's own buildup.</summary>
    private int parkEngaged;

    /// <summary>How long the park held, and what it saw when it released — printed, never asserted.</summary>
    private volatile string parkDiagnostics = "(park never engaged)";

    [HubFact]
    public async Task AHubDisposedBeforeItsBuildReturned_IsNotLeftInTheParentsRegistry()
    {
        var host = GetHost();
        var collection = host.ServiceProvider.GetRequiredService<HostedHubsCollection>();
        var address = new Address("child", "disposed-mid-build-" + Guid.NewGuid().AsString());

        // 1. The FIRST HubAdded for this address is the one Add raises from inside Build, before the
        //    buildup actions and before StartMessageProcessing — dispose the hub right there, on the
        //    constructing thread, exactly as a HubAdded subscriber in production would.
        using var disposeOnFirstRegistration = collection.HubAdded
            .Where(h => h.Address.Equals(address))
            .Take(1)
            .Subscribe(h => h.Dispose());

        // 2. Park the CONSTRUCTING thread in the hub's own synchronous buildup action — run by Build
        //    right after Add, outside any Post — until the pump (which drains the queued
        //    ShutdownRequest the instant it is posted) has taken the hub to Dead. Build then returns
        //    to the creation Lazy with a hub that has already run its own removal.
        var built = host.GetHostedHub(address, c => c.WithInitialization(hub =>
        {
            if (Interlocked.Exchange(ref parkEngaged, 1) != 0)
                return;
            var sw = Stopwatch.StartNew();
            try
            {
                // Bounded, and the bound is a DIAGNOSTIC rather than a budget: an empty,
                // never-started hub quiesces in milliseconds.
                SpinWait.SpinUntil(() => hub.RunLevel == MessageHubRunLevel.Dead, TimeSpan.FromSeconds(10));
            }
            finally
            {
                parkDiagnostics = $"parked {sw.ElapsedMilliseconds}ms in the hub's own buildup action; "
                                  + $"released at RunLevel={hub.RunLevel}";
            }
        }));

        Output.WriteLine($"PARK: {parkDiagnostics}");
        built.Should().NotBeNull("the creation path must still hand back the hub it built");
        parkEngaged.Should().Be(1,
            "the repro only means anything if the constructing thread was actually parked in the "
            + "hub's own buildup action — a park that never engaged makes this test a tautology");
        built!.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the park released only once the pump had taken the hub to Dead, so Build returned a corpse");

        // 3. Wait for the terminal signal the way every caller does, THEN ask the registry — the
        //    invariant is about what the parent says once the hub is provably gone.
        await built.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "a hub that reached Dead has signalled its disposal; the registry is asked only after that");

        host.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "a hosted hub that has completed its own disposal must not remain registered under its "
            + "address — a corpse the parent-chain walk still finds swallows every stream message and "
            + "user action routed there, with no refusal and no line (#4741)");
        collection.Hubs.Should().NotContain(h => h.Address.Equals(address),
            "the live snapshot must agree with the lookup — nothing keeps a Dead hub in the registry");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL for the registry itself. Every assertion above is satisfied by a
    /// registry that never held the hub at all; this one is not: the ordinary path — built, looked
    /// up, then disposed — must still register the hub while it lives, remove it when it goes, and
    /// mint a fresh activation for the released address.
    /// </summary>
    [HubFact]
    public async Task ALiveHubIsRegistered_RemovedWhenItGoes_AndItsAddressCanBeReactivated()
    {
        var host = GetHost();
        var collection = host.ServiceProvider.GetRequiredService<HostedHubsCollection>();
        var address = new Address("child", "successor-" + Guid.NewGuid().AsString());

        var first = host.GetHostedHub(address, c => c);
        host.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(first,
            "a live hub is registered under its address from the moment it is built");

        first.Dispose();
        await first.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the first hub's removal has to have RUN before a successor can be measured against it");
        host.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "the ordinary path removes a disposed hub — the fix must not have broken the removal");

        var successor = host.GetHostedHub(address, c => c);
        successor.Should().NotBeSameAs(first, "a fresh activation is minted for a released address");
        host.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(successor,
            "the successor is registered under the address the first hub released");
        collection.Hubs.Count(h => h.Address.Equals(address)).Should().Be(1,
            "exactly one hub is registered per address");
    }
}
