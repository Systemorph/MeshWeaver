using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// On Orleans a grain is activated by ANY call, so the question "is this hub instantiated?" is
/// answered inside the grain: a <see cref="DisposeRequest"/> that reaches an activation which never
/// built its hub is answered <c>Ignored</c> and builds nothing — observable as the address never
/// being ADDED to any silo's hosted-hub collection, which is what a built hub does
/// (<c>MessageHubGrain.CompleteActivation</c> → <c>meshHub.TryGetHostedHub</c>). A read then builds
/// the hub, and the same request recycles it.
/// </summary>
public class AColdGrainAnswersADisposeWithoutBuildingItsHubTest(ITestOutputHelper output)
    : OrleansMeshTestBase(output)
{
    /// <summary>
    /// 🚨 The window a NEGATIVE assertion always spends in full, so it is short — and derived, not
    /// guessed: <c>Quick</c> is already the "a local activation would have happened by now" class,
    /// and a grain's deferred hub build is one turn inside it. CI-scaled with everything else,
    /// which a literal could never be.
    /// </summary>
    private static TimeSpan NothingIsBuiltWithin => TestTimeouts.Quick / 4;

    private IMessageHub SiloMesh => SiloMeshAt(0);

    private IMessageHub SiloMeshAt(int index) =>
        ((InProcessSiloHandle)Cluster.Silos[index]).SiloHost.Services.GetRequiredService<IMessageHub>();

    /// <summary>
    /// The live hub for <paramref name="address"/> on whichever silo hosts it, or <c>null</c> when no
    /// silo does. A grain builds its hub as a HOSTED hub of its silo's mesh hub
    /// (<c>MessageHubGrain.CompleteActivation</c> → <c>meshHub.TryGetHostedHub</c>), and
    /// <c>HostedHubCreation.Never</c> is the read that creates nothing — the same read the router
    /// makes before it activates anything (<c>RoutingServiceBase.RouteInMesh</c>).
    /// </summary>
    private IMessageHub? LiveHub(Address address)
    {
        for (var i = 0; i < Cluster.Silos.Count; i++)
            if (SiloMeshAt(i).GetHostedHub(address, HostedHubCreation.Never) is { } hub)
                return hub;
        return null;
    }

    /// <summary>
    /// 🚨 The observable form of <see cref="LiveHub"/>: every silo's
    /// <c>HostedHubsCollection.HubAdded</c>, merged and filtered to this address. Same collections
    /// <see cref="LiveHub"/> reads, so "a hub was built for this address on SOME silo" has a signal
    /// rather than having to be inferred from a table read taken after an arbitrary pause.
    /// </summary>
    private IObservable<IMessageHub> HubBuiltOnAnySilo(Address address) =>
        Enumerable.Range(0, Cluster.Silos.Count)
            .Select(i => SiloMeshAt(i).ServiceProvider.GetRequiredService<HostedHubsCollection>().HubAdded)
            .Merge()
            .Where(h => (h.Address with { Host = null }).Equals(address));

    [Fact(Timeout = 120_000)]
    public async Task AColdGrain_BuildsNoHub_ForADispose_AndALiveOne_IsRecycled()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(110));
        var ct = deadline.Token;
        // Everything is issued from the silo's own mesh hub as SYSTEM: the node lives in a
        // partition no test identity holds a grant on, and the point of the test is the grain's
        // answer, not the access gate's. The dispose is posted from the node-operation issuing hub
        // — the survivor every documented recycle posts from (MeshOperations.Recycle).
        var access = SiloMesh.ServiceProvider.GetRequiredService<AccessService>();
        var issuing = SiloMesh.NodeOperationIssuingHub();

        var path = $"cold/{Guid.NewGuid():N}";
        var address = new Address(path);
        var meshService = SiloMesh.ServiceProvider.GetRequiredService<IMeshService>();

        // REPLAY-backed and connected BEFORE anything is posted: HubAdded is hot, and a grain
        // builds its hub on its own turn — a subscriber that attaches afterwards sees nothing and
        // a negative assertion over it would pass having observed nothing.
        var builtSomewhere = HubBuiltOnAnySilo(address).Replay(1);
        using var watching = builtSomewhere.Connect();

        await access.RunAsSystem(() => meshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = "A node nobody has opened",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        })).FirstAsync().Await(ct);
        LiveHub(address).Should().BeNull(
            "the precondition: creating a node builds no hub on any silo");

        // 1. A dispose to a COLD address builds nothing — asserted on the build signal itself.
        issuing.Post(new DisposeRequest { Reason = "cascade probe onto a cold grain" }, o => o.WithTarget(address));
        await builtSomewhere.Should().NotEmit(NothingIsBuiltWithin,
            "the grain answers a DisposeRequest before its deferred hub build runs — a recycle makes "
            + "an ACTIVATION re-read, and an address with none is already in the state a recycle "
            + "produces; a cascade over every instance of a type relies on this costing nothing",
            ct);
        LiveHub(address).Should().BeNull("…and every silo's hosted-hub table agrees");

        // 2. A read builds the hub (the signal fires); the same request then recycles it, and the
        //    next read builds a fresh one.
        (await access.RunAsSystem(() => SiloMesh.GetMeshNode(path, TestTimeouts.Convergence)).FirstAsync().Await(ct)).Should().NotBeNull();
        var live = await builtSomewhere.Should().Within(TestTimeouts.Convergence).Emit(
            "🚨 the POSITIVE CONTROL for the assertion above — the same merged stream, the same "
            + "filter, the same address. A read DOES build the hub, so this emits; without it a "
            + "NotEmit over a stream that can never emit would pass having proved nothing at all");
        LiveHub(address).Should().BeSameAs(live,
            "a read is what instantiates the hub, and the signal names the hub the silo holds");

        issuing.Post(new DisposeRequest { Reason = "recycle the live grain" }, o => o.WithTarget(address));
        await live.DisposalCompleted.FirstOrDefaultAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        live.RunLevel.Should().Be(MessageHubRunLevel.Dead, "a live hub IS torn down by the same request");

        (await access.RunAsSystem(() => SiloMesh.GetMeshNode(path, TestTimeouts.Convergence)).FirstAsync().Await(ct)).Should().NotBeNull();
        var reactivated = LiveHub(address);
        reactivated.Should().NotBeNull("the address comes back on the next access");
        reactivated.Should().NotBeSameAs(live, "…as a FRESH activation, which is the whole point of a recycle");
    }
}
