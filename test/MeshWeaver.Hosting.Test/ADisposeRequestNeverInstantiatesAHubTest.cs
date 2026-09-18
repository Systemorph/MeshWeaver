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
    private static readonly TimeSpan NothingHappensWindow = TimeSpan.FromSeconds(2);

    [Fact(Timeout = 60_000)]
    public async Task AColdAddress_StaysCold_AndALiveOne_IsRecycled()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "cold-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        var address = new Address(path);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
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

        // 1. A dispose to a COLD address. Negative test, no positive signal to filter for: the
        //    claim is that nothing is created, so the window is bounded and then read.
        GetClient().Post(new DisposeRequest { Reason = "cascade probe onto a cold address" },
            o => o.WithTarget(address));
        await Task.Delay(NothingHappensWindow, ct);
        Mesh.GetHostedHub(address, HostedHubCreation.Never).Should().BeNull(
            "a recycle makes an ACTIVATION re-read; an address with none is already in the state a "
            + "recycle produces, and building a hub only to tear it down is what a cascade over ten "
            + "thousand instances must never pay for");

        // 2. A read instantiates the hub; a dispose then recycles it; the next read reactivates.
        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
        var live = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        live.Should().NotBeNull("a read is what instantiates the hub");

        GetClient().Post(new DisposeRequest { Reason = "recycle the live hub" }, o => o.WithTarget(address));
        await live!.DisposalCompleted.FirstOrDefaultAsync().Timeout(ReadNodeTimeout).Await(ct);
        live.RunLevel.Should().Be(MessageHubRunLevel.Dead, "a live hub IS torn down by the same request");

        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
        var reactivated = Mesh.GetHostedHub(address, HostedHubCreation.Never);
        reactivated.Should().NotBeNull("the address comes back on the next access");
        reactivated.Should().NotBeSameAs(live, "…as a FRESH activation, which is the whole point of a recycle");
    }
}
