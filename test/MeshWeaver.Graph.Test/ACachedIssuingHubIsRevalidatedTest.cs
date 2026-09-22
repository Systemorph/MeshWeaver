using System;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.AI;
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

    /// <summary>
    /// 🚨 <b>THE SECOND SITE, controlled on its own.</b> <c>MeshOperations.ReadHub</c> has the same
    /// shape and its OWN cache and predicate, so a regression there would pass the test above and
    /// every static guard. This drives it through <c>ContentList</c>, a public read that goes
    /// through <c>ReadHub</c>, under ONE <c>MeshOperations</c> instance whose read hub is disposed
    /// underneath it.
    ///
    /// <para><b>Why the assertion is a bounded ABSENCE and not a value.</b> The read's target is an
    /// ordinary node with no content handler, so on a healthy read nothing answers until
    /// <c>ContentList</c>'s own 30 s timeout — waiting for that would make the test 30 s long and
    /// would measure the timeout rather than the seam. The DISCRIMINATOR is fast and one-sided:
    /// with the cache unrevalidated, <c>ReadHub.Observe</c> throws
    /// <see cref="System.ObjectDisposedException"/> SYNCHRONOUSLY inside the <c>SelectMany</c>, so
    /// the observable faults in milliseconds with <i>"is shutting down"</i>. So the test requires
    /// that no such answer arrives inside a short window: red in milliseconds when the cache is
    /// stale, green without waiting for the unrelated timeout.</para>
    ///
    /// <para>🚨 Its preconditions are what stop it being an assertion about nothing: the read hub
    /// must NOT be the mesh hub (off the router the seam is the identity function and there is no
    /// cached reference to go stale), it must reach <c>Dead</c>, and a successor must be minted at
    /// the same address.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AReadHubTornDownUnderneathTheFacade_DoesNotDisableEveryLaterRead()
    {
        var created = await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("CachedReadHubProbe", TestPartition)
            {
                Name = "read probe",
                NodeType = "Markdown"
            })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        // ONE facade, held across both reads — a fresh MeshOperations starts with an empty cache
        // and would resolve the successor anyway, hiding the defect.
        var ops = new MeshOperations(Mesh);

        // 🚨 AWAITED, not fire-and-forget. ReadHub is read INSIDE the path-resolution SelectMany,
        // so a subscription that has not yet resolved the path has not touched the cache — and a
        // teardown racing it would leave the cache EMPTY, so the second read would resolve the
        // successor for a reason that has nothing to do with the fix. The first version of this
        // test did exactly that and could not fail; the control caught it.
        var first = await ReadContent(ops, created.Path);
        Output.WriteLine($"DIAG first={first}");
        first.Should().Contain(CollectionMissing,
            "the first read must have REACHED the owning node hub and come back — that answer is "
            + "what proves the read seam was used and its hub cached. Anything else and the "
            + "measurement below is taken over an empty cache");

        var readHub = Mesh.ReadIssuingHub();
        readHub.Should().NotBeSameAs(Mesh,
            "this test is about the ROOT-hub branch, where the read seam hops onto "
            + "portal/reads-{meshId}; off the router it is the identity function and nothing is "
            + "cached that could go stale");
        var address = readHub.Address;

        readHub.Dispose();
        await readHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the read hub has to actually be down — a teardown that did nothing would leave "
                + "a perfectly usable cached reference and make the assertion below vacuous",
                cancellationToken: TestContext.Current.CancellationToken);
        readHub.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "and terminally down, not merely winding down");
        Mesh.ReadIssuingHub().Should().NotBeSameAs(readHub,
            "the registry mints a successor at the same address once the corpse has left it");
        Mesh.ReadIssuingHub().Address.Should().Be(address);

        Output.WriteLine($"DIAG readHub={address} runLevel={readHub.RunLevel}");

        var second = await ReadContent(ops, created.Path);
        Output.WriteLine($"DIAG second={second}");
        second.Should().Contain(CollectionMissing,
            "the SAME MeshOperations instance must keep reading after its read hub died, and get "
            + "the same real answer back through the successor. With the cache unrevalidated this "
            + "answers 'Hub portal/reads-… is shutting down — cannot register new response "
            + "subject' in about a millisecond instead");
    }

    /// <summary>The answer a read that REACHED the owning node hub comes back with, for a node that
    /// declares no such collection. Used as the positive signal on both sides of the teardown.</summary>
    private const string CollectionMissing = "collection 'content' not found";

    /// <summary>One <c>ContentList</c> through the facade, with a fault folded into the answer so
    /// both outcomes are one comparable string.</summary>
    private Task<string> ReadContent(MeshOperations ops, string path) =>
        ops.ContentList($"{path}/content")
            .Catch<string, Exception>(ex => Observable.Return($"FAULT {ex.GetType().Name}: {ex.Message}"))
            .Take(1)
            .Await(TestContext.Current.CancellationToken);
}
