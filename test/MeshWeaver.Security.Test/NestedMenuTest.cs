using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// The server-side normalization every renderer relies on: sub-menu children are sorted by
/// <c>Order</c> at every depth, and a grouping parent that has no surviving children is dropped
/// rather than rendered as a sub-menu that opens onto nothing.
///
/// <para>Both live in the aggregator on purpose. Menu items are permission-filtered by the PROVIDER,
/// never by the renderer, so a provider that gates each child individually can legitimately end up
/// emitting a parent whose children all vanished for this viewer — the empty-group case is a normal
/// outcome of access control, not a provider bug. And doing the sort here means <c>Order</c> means
/// the same thing on all four renderers instead of each re-sorting identically (which none of them
/// did — children came out in provider-append order).</para>
/// </summary>
public class NestedMenuTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "TestOrg/NestedMenuNode";

    /// <summary>A group whose children arrive DELIBERATELY out of order — 30, 10, 20.</summary>
    private static readonly NodeMenuItemDefinition UnsortedGroup = new(
        Label: "Tools",
        Area: NodeMenuItemDefinition.GroupArea,
        Icon: "🧰",
        Order: 60,
        Children:
        [
            new("Third", "ThirdArea", Icon: "3️⃣", Order: 30),
            new("First", "FirstArea", Icon: "1️⃣", Order: 10),
            new("Second", "SecondArea", Icon: "2️⃣", Order: 20),
        ]);

    /// <summary>
    /// A group the viewer may not see any child of — the shape a per-child permission gate produces
    /// once every child is filtered out.
    /// </summary>
    private static readonly NodeMenuItemDefinition EmptyGroup = new(
        Label: "NothingHere",
        Area: NodeMenuItemDefinition.GroupArea,
        Icon: "🕳️",
        Order: 61,
        Children: []);

    /// <summary>A group whose only child is itself an emptied group — pruning must run bottom-up.</summary>
    private static readonly NodeMenuItemDefinition NestedEmptyGroup = new(
        Label: "OuterEmpty",
        Area: NodeMenuItemDefinition.GroupArea,
        Icon: "🫙",
        Order: 62,
        Children: [new("InnerEmpty", NodeMenuItemDefinition.GroupArea, Icon: "🫗", Order: 1, Children: [])]);

    /// <summary>
    /// A parent that carries children AND a real Area of its own. It keeps a place in the menu even
    /// with no children left, because unlike a pure group it still has somewhere to go.
    /// </summary>
    private static readonly NodeMenuItemDefinition NavigableParentGoneEmpty = new(
        Label: "StillGoesSomewhere",
        Area: "RealArea",
        Icon: "🎯",
        Order: 63,
        Children: []);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode("TestOrg") { Name = "Test Organization" },
                new MeshNode("NestedMenuNode", "TestOrg") { Name = "Nested Menu Node" })
            .ConfigureDefaultNodeHub(c => c
                .AddDefaultLayoutAreas()
                .AddNodeMenuItems(
                    NodeMenuItemsExtensions.NodeMenuContext,
                    UnsortedGroup, EmptyGroup, NestedEmptyGroup, NavigableParentGoneEmpty));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private async Task<IReadOnlyList<NodeMenuItemDefinition>> NodeMenu()
    {
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(NodePath), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        // Match the emission that actually carries our provider's items — the menu renders
        // incrementally, so the first non-null snapshot is a partial one.
        var menu = await stream
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Should().Within(10.Seconds()).Match(
                x => x is MenuControl m && m.Items.Any(i => i.Label == UnsortedGroup.Label));

        var items = menu.Should().BeOfType<MenuControl>().Which.Items;
        foreach (var i in items)
            Output.WriteLine($"  {i.Icon} {i.Label} (Area={i.Area}, Order={i.Order}, Children={i.Children?.Count ?? 0})");
        return items;
    }

    [Fact(Timeout = 30000)]
    public async Task SubMenuChildren_AreSortedByOrder()
    {
        var group = (await NodeMenu()).Should().ContainSingle(i => i.Label == "Tools").Which;

        group.Children!.Select(c => c.Label).Should().Equal(["First", "Second", "Third"],
            "children are sorted by Order at every depth — they were emitted 30, 10, 20");
        group.Children!.Should().BeInAscendingOrder(c => c.Order);
    }

    [Fact(Timeout = 30000)]
    public async Task GroupWithNoSurvivingChildren_IsHidden()
    {
        var items = await NodeMenu();

        items.Select(i => i.Label).Should().NotContain("NothingHere",
            "a grouping parent whose children were all permission-filtered would open onto nothing");
    }

    [Fact(Timeout = 30000)]
    public async Task EmptyGroupPruning_RunsBottomUp()
    {
        var items = await NodeMenu();

        items.Select(i => i.Label).Should().NotContain("OuterEmpty",
            "a group whose only child was itself an emptied group is empty too");
    }

    [Fact(Timeout = 30000)]
    public async Task ParentWithItsOwnArea_SurvivesLosingEveryChild()
    {
        var items = await NodeMenu();

        var kept = items.Should().ContainSingle(i => i.Label == "StillGoesSomewhere").Which;
        kept.Area.Should().Be("RealArea");
        kept.IsSubmenuParent.Should().BeFalse(
            "with no children left it is an ordinary activatable entry, not a sub-menu");
    }
}
