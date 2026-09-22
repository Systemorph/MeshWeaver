using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The recycle cascade must enumerate its dependency network from a hub that OUTLIVES the hub
/// being recycled</b> — <see href="https://github.com/Systemorph/MeshWeaver/issues/5099">#5099</see>.
///
/// <para><b>The defect, measured in production.</b> A recycle of the NodeType
/// <c>Hosting/TriageItem</c> logged
/// <c>[RecycleCascade] … the recycle is INCOMPLETE. 0 address(es) were recycled, but 1 enumeration
/// leg(s) could not be read … (ObjectDisposedException: Instances cannot be resolved and nested
/// lifetimes cannot be created from this LifetimeScope as it (or one of its parent scopes) has
/// already been disposed.)</c>. The type's live instance hubs kept the assembly they were born
/// with — the exact stale-state problem the cascade was built to eliminate, reported as the
/// cascade's own success at every surface except that one log line.</para>
///
/// <para><b>Why the scope was dead.</b> <c>MessageHub.HandleDispose</c> runs the
/// <c>RecycleCascade</c> seam and THEN calls <c>Dispose()</c>, on the reasoning that the network is
/// derived "while this hub is still whole". That holds for the derivation's SYNCHRONOUS prologue and
/// for nothing after it: every enumeration leg is a cross-hub query, the legs run sequentially
/// (<c>Concat</c>), and by the time the second one subscribes the definition hub has finished
/// disposing and <c>HostedHubsCollection.CloseScopeWhenDisposed</c> has closed its Autofac lifetime
/// scope. The leg then resolves <c>AccessService</c> out of that scope
/// (<c>MeshService.StampViewer</c> → <c>CaptureContext</c>) and
/// <c>AutofacServiceProvider.GetService</c> throws.</para>
///
/// <para><b>The cure.</b> <see cref="NodeTypeRecycleCascade.DependencyNetwork"/> enumerates through
/// the mesh's READ-issuing hub (<c>MeshExtensions.ReadIssuingHub</c>), which is hosted by the mesh
/// hub and therefore untouched by the definition hub's teardown — the same "issue it from a
/// survivor" rule the cascade already followed for the outbound <c>DisposeRequest</c>s, applied to
/// the read half it had missed.</para>
///
/// <para>🚨 <b>Both halves of the window are covered, because the fix has two failure modes.</b>
/// Composition resolves the services (the prologue) and subscription runs the legs, and the hub can
/// die in either gap — so one case composes while the hub is alive and subscribes after it is dead
/// (the production shape exactly), and one calls the method when it is already dead. A third case
/// runs the ordinary live path: without it, a cascade that answered "complete, 0 addresses" for
/// every input would pass the other two having measured nothing, which is why every case here
/// asserts the instance path is IN the network and not merely that the answer is complete.</para>
/// </summary>
public class ARecycleCascadeEnumeratesFromASurvivorTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The NodeType whose dependency network is enumerated.</summary>
    private const string CascadeNodeType = "RecycleCascadeGadget";

    private const string InstanceId = "RecycleCascadeInstance";

    /// <summary>The one instance the enumeration must find — the denominator of every assertion below.</summary>
    private const string InstancePath = $"{TestPartition}/{InstanceId}";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(CascadeNodeType) { Name = "Recycle Cascade Gadget" },
                new MeshNode(InstanceId, TestPartition)
                {
                    Name = "Recycle Cascade Instance",
                    NodeType = CascadeNodeType,
                });

    /// <summary>
    /// The instrument's own calibration: with the caller's hub ALIVE, the enumeration finds the
    /// instance and reports a complete network. A failure here means the two regression cases below
    /// are measuring nothing — an empty network is "complete" too.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheNetwork_NamesTheInstance_WhenTheCallersHubIsAlive()
    {
        var donor = StandInForTheDefinitionHub("alive");

        var result = await NodeTypeRecycleCascade.DependencyNetwork(donor, CascadeNodeType)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the dependency network of a live NodeType must be derivable",
                cancellationToken: TestContext.Current.CancellationToken);

        result.Incomplete.Should().BeEmpty(
            "every enumeration leg answered, so nothing is unreachable");
        result.Addresses.Should().Contain(InstancePath,
            "the type has exactly one instance and the cascade exists to reach it — if this is "
            + "absent the enumeration read nothing and every 'complete' verdict below is vacuous");
    }

    /// <summary>
    /// 🚨 THE REGRESSION, in the production shape: the network is COMPOSED while the caller's hub is
    /// whole (as <c>HandleDispose</c> does, before <c>Dispose()</c>) and SUBSCRIBED after that hub
    /// has finished disposing and its DI lifetime scope has been closed (as the legs do, because
    /// each is a cross-hub query). RED before the fix — every leg fails with
    /// <c>ObjectDisposedException</c> and the cascade recycles nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheNetwork_IsStillDerivable_WhenTheCallersHubDiesBeforeTheLegsSubscribe()
    {
        var donor = StandInForTheDefinitionHub("dies-after-composition");

        // Composed while whole — this is the only moment HandleDispose's "still whole enough to
        // compute it" actually covers.
        var network = NodeTypeRecycleCascade.DependencyNetwork(donor, CascadeNodeType);

        await TearDownAndProveTheScopeIsClosed(donor);

        var result = await network
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a cascade composed on a hub that then died must still derive its network",
                cancellationToken: TestContext.Current.CancellationToken);

        result.Incomplete.Should().BeEmpty(
            "the enumeration must not depend on the DYING hub's DI scope — before the fix every "
            + "leg threw ObjectDisposedException out of AutofacServiceProvider.GetService and the "
            + "recycle reached 0 of its instance hubs while reporting a recycle had happened (#5099)");
        result.Addresses.Should().Contain(InstancePath,
            "and the instance really has to be in the answer — a complete-but-empty network is the "
            + "silent form of the same defect");
    }

    /// <summary>
    /// The other half of the window: the method is CALLED after the hub is already down. The
    /// prologue resolved its services from that hub too, so before the fix this throws
    /// <c>ObjectDisposedException</c> synchronously out of <see cref="NodeTypeRecycleCascade.DependencyNetwork"/>
    /// rather than producing an incomplete result — a different symptom of one cause, and the reason
    /// fixing only the leg subscription would leave a live hole.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheNetwork_IsStillDerivable_WhenTheCallersHubIsAlreadyDown()
    {
        var donor = StandInForTheDefinitionHub("already-down");
        await TearDownAndProveTheScopeIsClosed(donor);

        var result = await NodeTypeRecycleCascade.DependencyNetwork(donor, CascadeNodeType)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("deriving the network must not resolve anything out of the recycled hub's scope",
                cancellationToken: TestContext.Current.CancellationToken);

        result.Incomplete.Should().BeEmpty("no leg may fail");
        result.Addresses.Should().Contain(InstancePath, "and the instance must be found");
    }

    /// <summary>
    /// A hub standing in for the NodeType definition hub: a hosted hub of the mesh, so it OWNS a
    /// child lifetime scope that <c>HostedHubsCollection.CloseScopeWhenDisposed</c> closes when it
    /// goes down. That ownership is the whole mechanism under test — a hub sharing the mesh's own
    /// container could not reproduce it.
    /// </summary>
    /// <param name="discriminator">Keeps each case's hub distinct within a shared mesh.</param>
    private IMessageHub StandInForTheDefinitionHub(string discriminator)
    {
        var donor = Mesh.GetHostedHub(
            new Address("portal", $"recycle-cascade-donor-{discriminator}"),
            config => config,
            HostedHubCreation.Always);
        donor.Should().NotBeNull("the stand-in definition hub must exist");
        return donor!;
    }

    /// <summary>
    /// Disposes <paramref name="donor"/>, waits for its own terminal signal, and then PROVES the
    /// precondition the regression cases rest on: resolving out of that hub's provider now throws.
    /// Asserted rather than assumed — a scope that happened to stay open would make both cases pass
    /// without exercising anything, which is the failure mode this whole file is about.
    /// </summary>
    /// <param name="donor">The stand-in definition hub to tear down.</param>
    private static async Task TearDownAndProveTheScopeIsClosed(IMessageHub donor)
    {
        var down = donor.DisposalCompleted.Take(1);
        donor.Dispose();
        await down.Should().Within(TestTimeouts.Convergence)
            .Emit("the stand-in definition hub must actually finish disposing",
                cancellationToken: TestContext.Current.CancellationToken);

        // HostedHubsCollection closes the scope on DisposalCompleted, ahead of this subscriber, so
        // by here the provider is closed. Asserting it keeps the cases from going vacuous if that
        // ordering ever changes.
        Action resolveFromTheDeadHub = () => donor.ServiceProvider.GetService<ILoggerFactory>();
        resolveFromTheDeadHub.Should().Throw<ObjectDisposedException>(
            "the point of these cases is that the recycled hub's DI scope is GONE — if it still "
            + "resolves, they prove nothing about #5099");
    }
}
