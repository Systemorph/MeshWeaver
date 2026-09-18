using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A <see cref="DisposeRequest"/> to an address with no live hub instantiates nothing — the router
/// answers it without creating a hub (the sub-bits of a recycle cascade are recycled only if they
/// were instantiated in the first place) — while a live hub is torn down and the address reactivates
/// on the next read.
/// </summary>
public class ADisposeRequestNeverInstantiatesAHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// 🚨 The window a NEGATIVE assertion always spends in full, so it is short — and derived, not
    /// guessed: <c>Quick</c> is already the "a local activation would have happened by now" class,
    /// and building a hub is one router turn inside it. CI-scaled with everything else, which a
    /// literal could never be.
    /// </summary>
    private static TimeSpan NothingIsBuiltWithin => TestTimeouts.Quick / 4;

    // 🚨 Timeout doubled from 60 s when the sleep became an observable wait: ReadNodeTimeout is
    // itself 60 s, so the outer bound could never dominate the inner one and a slow read could only
    // ever be reported as an anonymous xunit kill (TestTimeouts, "the inner bound must be strictly
    // less than the outer one"). Matches the Orleans sibling.
    [Fact(Timeout = 120_000)]
    public async Task AColdAddress_StaysCold_AndALiveOne_IsRecycled()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "cold-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        var address = new Address(path);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // 🚨 The ACTIVATION SIGNAL, not a sleep. HostedHubsCollection.HubAdded emits each hub as it
        // is added, and it is the very collection `Mesh.GetHostedHub(address, …Never)` reads — so
        // "the router built a hub for this address" has an observable form and does not have to be
        // inferred from a registry read taken after an arbitrary pause. REPLAY-backed and connected
        // HERE, before anything is posted: the subject is hot and a hub is built on the router's own
        // turn, which can complete before a later subscriber attaches (the shape that made
        // UserActionOutlivesStreamReleaseTest pass having observed nothing).
        var builtForThisAddress = Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .HubAdded
            .Where(h => (h.Address with { Host = null }).Equals(address))
            .Replay(1);
        using var watching = builtForThisAddress.Connect();

        await access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode(id, TestPartition)
            {
                NodeType = "Markdown",
                Name = "A node nobody has opened",
                State = MeshNodeState.Active,
            }))
            .Timeout(ReadNodeTimeout)
            .FirstAsync()
            .Await(ct);
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "the precondition: creating a node does not activate its hub");

        // 1. A dispose to a COLD address builds nothing — asserted on the build signal itself.
        GetClient().Post(new DisposeRequest { Reason = "cascade probe onto a cold address" },
            o => o.WithTarget(address));
        await builtForThisAddress.Should().NotEmit(NothingIsBuiltWithin,
            "a recycle makes an ACTIVATION re-read; an address with none is already in the state a "
            + "recycle produces, and building a hub only to tear it down is what a cascade over ten "
            + "thousand instances must never pay for",
            ct);
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "…and the registry the signal is fed from agrees");

        // 2. A read instantiates the hub; a dispose then recycles it; the next read reactivates.
        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
        var live = await builtForThisAddress.Should().Within(TestTimeouts.Quick).Emit(
            "🚨 the POSITIVE CONTROL for the assertion above — the same stream, the same filter, the "
            + "same address. A read DOES build the hub, so this emits; without it a NotEmit over a "
            + "stream that can never emit would pass having proved nothing at all");
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeSameAs(live,
            "a read is what instantiates the hub, and the signal names the hub the registry holds");

        GetClient().Post(new DisposeRequest { Reason = "recycle the live hub" }, o => o.WithTarget(address));
        await live.DisposalCompleted.FirstOrDefaultAsync().Timeout(ReadNodeTimeout).Await(ct);
        live.RunLevel.Should().Be(MessageHubRunLevel.Dead, "a live hub IS torn down by the same request");

        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
        var reactivated = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        reactivated.Should().NotBeNull("the address comes back on the next access");
        reactivated.Should().NotBeSameAs(live, "…as a FRESH activation, which is the whole point of a recycle");
    }
}
