using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

        using var constructionEntered = new ManualResetEventSlim(false);
        using var releaseConstruction = new ManualResetEventSlim(false);

        // Kick the creation off a background thread: the WithInitialization hook runs
        // synchronously inside MessageHubConfiguration.Build, so this thread parks inside
        // construction — exactly where the routed-emission creations of the straggler class
        // sit when teardown begins.
        var creation = Task.Run(() => client.GetHostedHub(
            new Address("inflight", "1"),
            c => c.WithInitialization(_ =>
            {
                constructionEntered.Set();
                releaseConstruction.Wait(TimeSpan.FromSeconds(30));
            }),
            HostedHubCreation.Always));

        constructionEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the hosted-hub construction must have started before disposal begins");

        var disposalCompleted = client.DisposalCompleted.Take(1).ToTask();
        client.Dispose();

        // Negative wait (sanctioned "confirm nothing happened" shape): disposal must NOT
        // complete while the construction it started is still in flight — completing here is
        // the bug, because the owner would proceed to tear down the container under the
        // running Build.
        var winner = await Task.WhenAny(disposalCompleted, Task.Delay(TimeSpan.FromSeconds(1)));
        winner.Should().NotBe(disposalCompleted,
            "disposal must wait for the in-flight hosted-hub construction to finish");

        releaseConstruction.Set();

        // Now disposal finishes the started request and completes.
        await disposalCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        // And the late-constructed hub was disposed with the collection — not leaked as a
        // zombie outside the disposal snapshot.
        var lateHub = await creation.WaitAsync(TimeSpan.FromSeconds(10));
        lateHub.Should().NotBeNull("the in-flight creation was started before disposal and must be finished, not refused");
        await lateHub!.DisposalCompleted.Take(1).ToTask().WaitAsync(TimeSpan.FromSeconds(10));
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
