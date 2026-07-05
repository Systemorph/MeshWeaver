using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Verifies the "Invite people" NODE-menu item (<see cref="SpaceInviteMenuProvider"/>) appears on a
/// Space and is absent on a non-Space node — the provider self-gates on <c>NodeType == "Space"</c>
/// (plus Update), so it never leaks onto ordinary nodes.
/// </summary>
public class SpaceInviteMenuTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "InviteMenuSpace";
    private const string Plain = "InviteMenuPlain";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddMeshNodes(
            new MeshNode(Space) { Name = "Space", NodeType = "Space" },
            new MeshNode(Plain) { Name = "Plain" });   // no NodeType → not a Space

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private IObservable<IReadOnlyList<NodeMenuItemDefinition>> NodeMenu(Address nodeAddress)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Where(x => x is MenuControl)
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)((MenuControl)x!).Items);

    [Fact(Timeout = 60000)]
    public async Task InviteItem_ShownOnSpace()
    {
        // Admin (auto-logged-in) has Update on the Space → the item appears.
        var items = await NodeMenu(new Address(Space))
            .Where(i => i.Any(m => m.Label == "Invite people"))
            .FirstAsync().Timeout(30.Seconds());
        Assert.Contains(items, m => m.Label == "Invite people"
            && m.Area == SpaceInviteLayoutArea.AreaName);
    }

    [Fact(Timeout = 60000)]
    public async Task InviteItem_HiddenOnNonSpace()
    {
        // Wait until the node menu has rendered (Edit is a standard item), then assert Invite is absent.
        var items = await NodeMenu(new Address(Plain))
            .Where(i => i.Any(m => m.Label == "Edit"))
            .FirstAsync().Timeout(30.Seconds());
        Assert.DoesNotContain(items, m => m.Label == "Invite people");
    }
}
