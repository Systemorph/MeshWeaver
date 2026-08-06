#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins where node CRUD is TARGETED. The mesh hub is the router: running create/delete/move on its
/// action block starves real routing traffic and wedges the portal (atioz 2026-06-11 —
/// "11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/&lt;self&gt; stale &gt;60s"). So a hub that
/// declared itself an execution target must serve its own writes, and everything else keeps falling
/// back to the router exactly as before.
/// </summary>
public class NodeOperationTargetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMessageHub ExecutionHub(string session) =>
        Mesh.GetHostedHub(
            AddressExtensions.CreatePortalAddress(session),
            c => c
                .AddData()
                .WithNodeOperationExecution()
                .WithInitialization(h => h.RegisterForDisposal(RoutingService.RegisterStream(h))),
            HostedHubCreation.Always)!;

    [Fact]
    public void PortalHubThatDeclaredExecution_TargetsItself_NotTheRouter()
    {
        var portal = ExecutionHub("session-portal");

        var target = portal.NodeOperationTarget();

        target.Should().Be(portal.Address, "a hub that opted in serves its own writes");
        target.Type.Should().NotBe("mesh", "the router must never be the work target");
    }

    [Fact]
    public void HubWithoutTheOptIn_FallsBackToTheMeshRoot()
    {
        // A plain client hub never opted in, and neither does anything between it and the root —
        // the fallback keeps such hosts working exactly as before this change.
        var client = GetClient();

        client.NodeOperationTarget().Should().Be(Mesh.Address);
    }

    [Fact]
    public void ChildOfAnExecutionHub_WalksUpToThatHub_NotPastItToTheRouter()
    {
        var portal = ExecutionHub("session-parent");

        // A child hub (the shape a Blazor circuit / MCP session creates under the portal) has no
        // opt-in of its own; the walk must stop at its nearest declaring ancestor.
        var child = portal.GetHostedHub(new Address("child", "c1"), c => c, HostedHubCreation.Always)!;

        child.NodeOperationTarget().Should().Be(portal.Address);
    }

    /// <summary>
    /// 🚨 The regression this whole design hinges on. A per-node hub registers the node-operation
    /// HANDLERS (every hub with AddMeshDataSource does — it must be able to receive ops addressed to
    /// its own node, e.g. the delete fan-out's <c>DeleteNodeRequest</c> at <c>new Address(path)</c>).
    /// If "has handlers" were also read as "is an execution target", a per-node hub would target
    /// ITSELF — and per-node hubs are exactly the ones carrying AddAccessControlPipeline, whose
    /// CreateNodePermissionAttribute anchors its check at the RECEIVING HUB's path. A learner
    /// copying a course module into their own partition was then denied "lacks Create permission on
    /// 'TestCourse/Module1/Ex1'": Create evaluated on the read-only source they were merely
    /// rendering (EducationStartExerciseTest). Silent privilege reduction — so the two capabilities
    /// stay two markers.
    /// </summary>
    [Fact]
    public void HubWithHandlersButNoExecutionOptIn_StillFallsBackToTheRouter()
    {
        // Exactly the shape AddMeshDataSource gives every per-node hub: the handlers, nothing more.
        var nodeShaped = Mesh.GetHostedHub(
            new Address(TestPartition, "target-node"),
            c => c.AddData().WithNodeOperationHandlers(),
            HostedHubCreation.Always)!;

        nodeShaped.NodeOperationTarget().Should().Be(
            Mesh.Address,
            "a node hub handles ops for its OWN node; it is not a work queue for someone else's write");
    }
}
