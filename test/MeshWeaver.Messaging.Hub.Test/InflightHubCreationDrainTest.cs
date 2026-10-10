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
/// Deterministic repro for the in-flight-construction teardown race (#613's trigger; the
/// "check-then-act residue" #488 named): a hosted-hub creation that passed the IsDisposing
/// check keeps building while the owner disposes. Before the drain leg in
/// <c>HostedHubsCollection.DisposeHubsReactive</c>, the owner signalled DisposalCompleted —
/// and the host then tore down the DI container — while the Build was still resolving
/// services, which is the ObjectDisposedException straggler class locally and the
/// TypeRegistry-over-unloading-ALC SIGSEGV on CI.
///
/// <para>The contract under test: disposal FINISHES the requests already started (waits for
/// the in-flight construction, then disposes whatever it produced — no zombie) and refuses
/// new ones. The gate/latch pair below is the sanctioned deterministic-concurrency-repro
/// shape: the latch proves construction is mid-flight before disposal starts, the gate
/// holds it there until the test has asserted disposal is waiting.</para>
/// </summary>
public class InflightHubCreationDrainTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public async Task Disposal_WaitsForInflightConstruction_ThenDisposesTheLateHub()
    {
        var client = GetClient();

        // 🚨 No hand-woven gate. `constructionEntered` travels construction → test, so it is an
        // AsyncSubject the producer completes and the test awaits through the assertion helpers;
        // the release travels INTO the thread parked inside Build, so it is a volatile flag polled
        // under a bounded SpinUntil and written in the `finally` below — the shape that stops a
        // failing assertion from stranding that thread for the full 30 s.
        var constructionEntered = new AsyncSubject<Unit>();
        var releaseConstruction = 0;

        // Kick the creation off a background thread: the WithInitialization hook runs
        // synchronously inside MessageHubConfiguration.Build, so this thread parks inside
        // construction — exactly where the routed-emission creations of the straggler class
        // sit when teardown begins.
        var creation = Task.Run(() => client.GetHostedHub(
            new Address("inflight", "1"),
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

            // Negative wait (sanctioned "confirm nothing happened" shape): disposal must NOT
            // complete while the construction it started is still in flight — completing here is
            // the bug, because the owner would proceed to tear down the container under the
            // running Build.
            var winner = await Task.WhenAny(disposalCompleted, Task.Delay(TimeSpan.FromSeconds(1)));
            winner.Should().NotBe(disposalCompleted,
                "disposal must wait for the in-flight hosted-hub construction to finish");

            Volatile.Write(ref releaseConstruction, 1);

            // Now disposal finishes the started request and completes.
            await disposalCompleted.WaitAsync(TimeSpan.FromSeconds(10));

            // And the late-constructed hub was disposed with the collection — not leaked as a
            // zombie outside the disposal snapshot.
            var lateHub = await creation.WaitAsync(TimeSpan.FromSeconds(10));
            lateHub.Should().NotBeNull("the in-flight creation was started before disposal and must be finished, not refused");
            await lateHub!.DisposalCompleted.Take(1).Await().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Volatile.Write(ref releaseConstruction, 1);
        }
    }

    /// <summary>
    /// A hub declared <c>WithTeardownAfterSiblings</c> whose construction finishes AFTER the
    /// owner's teardown began is still a dependency: it is disposed in the second wave, after its
    /// siblings, never by the in-flight leg while a sibling is still tearing down (#6078). This is
    /// the first-time node-stream cache resolution overlapping a teardown.
    ///
    /// <para>The sibling's teardown is held deterministically by a construction parked inside the
    /// SIBLING's own hosted collection: the sibling's disposal joins that in-flight creation, so it
    /// cannot finish until the test releases it. The dependency's construction is released first;
    /// the in-flight leg runs synchronously on that release (the creation's <c>finally</c> pings
    /// before returning), so once the creation has returned, the leg has decided.</para>
    ///
    /// <para><b>Negative control.</b> With the in-flight leg disposing every late hub at once,
    /// the late dependency reads <c>IsDisposing</c> while its sibling is still held, and this case
    /// goes red. Without <c>InheritCreationFreeze</c> the late dependency reads
    /// <c>IsShuttingDown == false</c> and accepts a new hosted hub, and it goes red there.</para>
    /// </summary>
    [Fact]
    public async Task ALateDependency_IsDisposedOnlyAfterItsSiblings()
    {
        var client = GetClient();
        var sibling = client.GetHostedHub(new Address("sibling", "1"), c => c, HostedHubCreation.Always);
        sibling.Should().NotBeNull();

        var childEntered = new AsyncSubject<Unit>();
        var dependencyEntered = new AsyncSubject<Unit>();
        var releaseChild = 0;
        var releaseDependency = 0;

        // Holds the SIBLING's teardown: its own hosted collection waits for this construction.
        var childCreation = Task.Run(() => sibling!.GetHostedHub(
            new Address("siblingchild", "1"),
            c => c.WithInitialization(_ =>
            {
                childEntered.OnNext(Unit.Default);
                childEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseChild) == 1, TestTimeouts.Convergence);
            }),
            HostedHubCreation.Always));

        // The dependency, mid-construction when the owner's teardown begins. Parked in its
        // CONFIGURATION function, which runs before the hub's constructor registers it with the
        // owner, so the owner's teardown snapshot cannot hold it: it is a LATE hub.
        var dependencyCreation = Task.Run(() => client.GetHostedHub(
            new Address("dependency", "1"),
            c =>
            {
                dependencyEntered.OnNext(Unit.Default);
                dependencyEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseDependency) == 1, TestTimeouts.Convergence);
                return c.WithTeardownAfterSiblings();
            },
            HostedHubCreation.Always));

        try
        {
            await childEntered.Should().Within(10.Seconds()).Emit(
                "the sibling's child construction must be in flight, so the sibling's teardown is held");
            await dependencyEntered.Should().Within(10.Seconds()).Emit(
                "the dependency's construction must be in flight before the owner's teardown begins");

            var disposalCompleted = client.DisposalCompleted.Take(1).Await();
            client.Dispose();

            // The owner's teardown has snapshotted its hubs and the first wave reached the sibling,
            // which now sits in its own hosted-hub join. The dependency is still mid-construction,
            // so it is a LATE hub of the owner's teardown.
            await sibling!.RunLevelChanged.Where(l => l >= MessageHubRunLevel.DisposeHostedHubs)
                .Should().Within(10.Seconds()).Emit(
                    "the owner's first wave must have reached the sibling before the dependency is released");

            Volatile.Write(ref releaseDependency, 1);
            var lateDependency = await dependencyCreation.WaitAsync(TimeSpan.FromSeconds(10));
            lateDependency.Should().NotBeNull("a creation started before the teardown is finished, not refused");

            lateDependency!.IsDisposing.Should().BeFalse(
                "a late hub declared WithTeardownAfterSiblings goes in the second wave, after its "
                + "sibling, whose teardown is still held by its own in-flight construction");

            // ...but it is part of the owner's shutdown from the moment it registers. It finished
            // constructing after CloseCreation walked the owner's hubs, so it was not in that
            // cascade; without inheriting the freeze it would sit through the whole first wave
            // reading IsShuttingDown == false, its own hosted collection still open, free to start
            // descendant work after the owner's teardown began.
            lateDependency.IsShuttingDown.Should().BeTrue(
                "a hub that finishes construction under a frozen owner inherits the freeze");
            lateDependency.GetHostedHub(new Address("dependencychild", "1"), c => c, HostedHubCreation.Always)
                .Should().BeNull("creation beneath a late dependency is refused while it waits for "
                                 + "the second wave, exactly as beneath every snapshotted hub");
            lateDependency.IsDisposing.Should().BeFalse(
                "precondition: the refusal above came from the inherited freeze, not from the "
                + "dependency's own disposal having begun");

            Volatile.Write(ref releaseChild, 1);
            await disposalCompleted.WaitAsync(TimeSpan.FromSeconds(10));
            await lateDependency.DisposalCompleted.Take(1).Await().WaitAsync(TimeSpan.FromSeconds(10));
            await childCreation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Volatile.Write(ref releaseDependency, 1);
            Volatile.Write(ref releaseChild, 1);
        }
    }

    [Fact]
    public void Creation_AfterDisposalBegan_IsRefused()
    {
        var client = GetClient();
        client.Dispose();

        // The other half of the contract: NEW requests after disposal began are refused,
        // transparently (null — the caller's dead-stream handling takes over), never a
        // half-built hub on a dying container.
        var refused = client.GetHostedHub(
            new Address("late", "1"),
            c => c,
            HostedHubCreation.Always);

        refused.Should().BeNull("creation after disposal began must be refused");
    }
}
