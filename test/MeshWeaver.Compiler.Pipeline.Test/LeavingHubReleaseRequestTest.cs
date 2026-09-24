using System;
using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Systemorph/MeshWeaver#5629 — the NodeType RELEASE leg on a hub that is LEAVING.
///
/// <para><b>What was measured.</b> A memex pod draining at 2026-09-24 00:49:10Z logged fifteen
/// <c>[Recompile] Release request for Manufacturing/… failed — a TRANSIENT HUB OR ROUTING MISS
/// ended the write: … Host is shutting down, cannot route to …</c> lines in 3 ms: a GitSync
/// recompile wave issued trigger writes from a host whose own router refuses every outbound
/// route. Nothing could have landed, and each attempt was filed at Error as a routing fault.</para>
///
/// <para><b>The rule (#3129, extended to the release leg):</b> a leaving hub does not start a
/// release. <see cref="NodeTypeReleaseExtensions.ObserveNodeTypeRelease(IMessageHub, string, bool, string, Action{string}, Action{NodeTypeReleaseRefusal})"/>
/// answers <c>false</c> with the <see cref="NodeTypeReleaseFailure.HostLeaving"/> class and the
/// shared record is untouched. The control arm is the SAME call against the SAME node from a hub
/// that stays, which stamps the trigger — without it the leaving arm would pass just as well
/// against a release leg that had stopped writing altogether.</para>
/// </summary>
public class LeavingHubReleaseRequestTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>A child of the mesh, so disposing it flips ITS <see cref="IMessageHub.IsShuttingDown"/>
    /// while the mesh and the NodeType's owner keep running — the shape
    /// <c>LeavingHubAdoptionSweepTest</c> uses.</summary>
    private IMessageHub ReleaseHub(string id) =>
        Mesh.GetHostedHub(
            new Address("release-wave", id),
            c => c.AddData().WithGraphTypes(),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

    private async Task CreateType(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath[(typePath.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
        };
        await MeshService.CreateNode(typeNode)
            .Should().Within(20.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is not null,
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The release as the test's own identity — the DevLogin platform admin the base
    /// class signs in, who holds Compile — so the permission check is not what separates the two
    /// arms. The caller's context is captured at SUBSCRIBE, exactly as the product documents.</summary>
    private static IObservable<bool> Release(
        IMessageHub hub, string typePath, ConcurrentQueue<NodeTypeReleaseRefusal> refusals) =>
        hub.ObserveNodeTypeRelease(typePath,
            force: false, releaseNotes: null, onError: null, onRefused: refusals.Enqueue);

    private bool Triggered(MeshNode? n) =>
        n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { RequestedReleaseAt: not null };

    [Fact]
    public async Task ALeavingHub_DoesNotIssueTheRelease_AndSaysWhy_WhileALiveHubStampsIt()
    {
        const string typePath = "type/LeavingReleaseType";
        await CreateType(typePath);

        // THE LEAVING ARM.
        var leaving = ReleaseHub("leaving");
        leaving.Dispose();
        leaving.IsLeaving().Should().BeTrue("Dispose() begins this hub's teardown synchronously");

        var leavingRefusals = new ConcurrentQueue<NodeTypeReleaseRefusal>();
        var released = await Release(leaving, typePath, leavingRefusals)
            .Should().Within(20.Seconds())
            .Emit("a leaving hub still ANSWERS the caller — a release wave must never park on it");
        released.Should().BeFalse("nothing is released from a host that is leaving");
        leavingRefusals.Should().ContainSingle().Which.Failure.Should().Be(
            NodeTypeReleaseFailure.HostLeaving,
            "the refusal names its real cause — a leaving host — not a routing miss that reads as a "
            + "defect (#5629's fifteen fail-level lines)");

        await Mesh.GetMeshNodeStream(typePath).Where(Triggered)
            .Should()
            .NotEmit(3.Seconds(), "a leaving hub issues no trigger write on the shared NodeType record");

        // THE CONTROL ARM — the same call, the same node, the same identity, from a hub that
        // stays: the mesh hub. (A bare child hub is not used here because it resolves the
        // signed-in user's effective permissions without Compile — "All" against the mesh's
        // "All, Compile" — so it would be refused for a reason that has nothing to do with leaving.)
        var live = Mesh;
        live.IsLeaving().Should().BeFalse();
        var liveRefusals = new ConcurrentQueue<NodeTypeReleaseRefusal>();
        var releasedLive = await Release(live, typePath, liveRefusals)
            .Should().Within(30.Seconds()).Emit("a live hub's release request completes");
        releasedLive.Should().BeTrue(
            "on a live hub the same request IS issued — without this arm the leaving arm would "
            + "prove nothing. Refusals: " + string.Join("; ", liveRefusals));
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(Triggered, "the live hub's trigger lands on the shared record");
    }
}
