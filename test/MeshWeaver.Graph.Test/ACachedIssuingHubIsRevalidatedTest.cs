using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A cached hub REFERENCE outlives the activation it names, and the registry's
/// retire-and-replace cannot reach it.</b>
///
/// <para><b>The defect.</b> <c>MeshService</c> resolved its issuing hub once —
/// <c>_issuingHub ??= hub.NodeOperationIssuingHub()</c> — and kept that reference for the service's
/// whole lifetime. On the root mesh hub that reference is <c>portal/nodeops-{meshId}</c>, an
/// ordinary hosted hub that can die underneath it: a routed <c>DisposeRequest</c>, a recycle, a
/// teardown that wedges. A hub past <c>Started</c> can serve nothing — its intake refuses every
/// delivery and a direct <c>Observe(...)</c> faults — so from that moment every node operation the
/// service issues is answered <i>"hub … is shutting down"</i>, for as long as the service lives,
/// with nothing short of a process restart to recover it.</para>
///
/// <para><b>Why #5136's fix does not cover it.</b> Retire-and-replace takes a hub at
/// <c>RunLevel &gt;= ShutDown</c> out from under its ADDRESS so the next LOOKUP mints a successor.
/// That is a fix to the registry; it cannot reach a reference somebody has already put in a field.
/// The two halves are complementary, and this is the second one.</para>
///
/// <para><b>Controls on both sides.</b> The first create is asserted to SUCCEED before anything is
/// torn down, so a run in which the service never worked at all cannot read as a pass; the hub is
/// asserted to have actually reached <c>Dead</c>, so a teardown that silently did nothing cannot
/// either; and the successor is asserted to be a different instance at the same address, which is
/// what makes "the second create succeeded" mean the cache was revalidated rather than that nothing
/// ever changed. The measured control on the unfixed build (the <c>??=</c> restored) is reported in
/// the pull request.</para>
/// </summary>
public class ACachedIssuingHubIsRevalidatedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// 🚨 THE MEASUREMENT. One <see cref="IMeshService"/> instance — the shape every mesh-singleton
    /// consumer has, a Scoped service resolved from the root hub's provider — issues a create, its
    /// issuing hub is torn down underneath it, and it must issue the next create successfully.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnIssuingHubTornDownUnderneathTheService_DoesNotDisableEveryLaterOperation()
    {
        // ONE instance, held across both operations: resolving twice would hide the defect, because
        // a fresh MeshService starts with an empty cache and would resolve the successor anyway.
        var service = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var first = await service
            .CreateNode(new MeshNode("CachedIssuingHubProbeBefore", TestPartition)
            {
                Name = "before",
                NodeType = "Markdown"
            })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);
        first.Path.Should().Be($"{TestPartition}/CachedIssuingHubProbeBefore",
            "the service must be WORKING before its issuing hub is torn down — otherwise the "
            + "second create failing would say nothing about the cache");

        var issuing = Mesh.NodeOperationIssuingHub();
        issuing.Should().NotBeSameAs(Mesh,
            "this test is about the ROOT-hub branch, where the seam hops onto portal/nodeops-{meshId}; "
            + "off the router it is the identity function and there is no cached reference to go stale");
        var address = issuing.Address;

        issuing.Dispose();
        await issuing.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the issuing hub has to actually be down — a teardown that did nothing would "
                + "leave a perfectly usable cached reference and make the assertion below vacuous",
                cancellationToken: TestContext.Current.CancellationToken);
        issuing.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "and it has to be terminally down, not merely winding down");

        var successor = Mesh.NodeOperationIssuingHub();
        successor.Should().NotBeSameAs(issuing,
            "the registry mints a successor at the same address once the corpse has left it — that "
            + "successor is what a revalidated cache must pick up");
        successor.Address.Should().Be(address,
            "the ADDRESS is the stable thing, which is why MeshService may cache it outright and "
            + "may not cache the hub");

        Output.WriteLine($"DIAG issuing={issuing.Address} runLevel={issuing.RunLevel} "
            + $"successorIsSame={ReferenceEquals(issuing, successor)}");

        var second = await service
            .CreateNode(new MeshNode("CachedIssuingHubProbeAfter", TestPartition)
            {
                Name = "after",
                NodeType = "Markdown"
            })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        second.Path.Should().Be($"{TestPartition}/CachedIssuingHubProbeAfter",
            "the SAME IMeshService instance must keep working after its issuing hub died: a cached "
            + "hub reference has to be revalidated at the read, because the registry's "
            + "retire-and-replace acts on the address and cannot reach a reference in a field");
    }
}
