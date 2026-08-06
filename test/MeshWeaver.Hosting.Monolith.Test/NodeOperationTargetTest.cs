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
/// opted into the node-operation handlers must serve its own writes, and only a host with no such hub
/// anywhere up the chain may fall back to the router.
/// </summary>
public class NodeOperationTargetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMessageHub HubWithHandlers(string session) =>
        Mesh.GetHostedHub(
            AddressExtensions.CreatePortalAddress(session),
            c => c
                .AddData()
                .WithNodeOperationHandlers()
                .WithInitialization(h => h.RegisterForDisposal(RoutingService.RegisterStream(h))),
            HostedHubCreation.Always)!;

    [Fact]
    public void PortalHubThatHandlesNodeOps_TargetsItself_NotTheRouter()
    {
        var portal = HubWithHandlers("session-portal");

        var target = portal.NodeOperationTarget();

        target.Should().Be(portal.Address, "a hub that registered the handlers serves its own writes");
        target.Type.Should().NotBe("mesh", "the router must never be the work target");
    }

    [Fact]
    public void HubWithoutHandlers_FallsBackToTheMeshRoot()
    {
        // A plain client hub never opted in, and neither does anything between it and the root —
        // the fallback keeps such hosts working exactly as before this change.
        var client = GetClient();

        client.NodeOperationTarget().Should().Be(Mesh.Address);
    }

    [Fact]
    public void ChildOfAHandlingHub_WalksUpToThatHub_NotPastItToTheRouter()
    {
        var portal = HubWithHandlers("session-parent");

        // A child hub (the shape a Blazor circuit / MCP session creates under the portal) has no
        // handlers of its own; the walk must stop at its nearest handling ancestor.
        var child = portal.GetHostedHub(new Address("child", "c1"), c => c, HostedHubCreation.Always)!;

        child.NodeOperationTarget().Should().Be(portal.Address);
    }
}
