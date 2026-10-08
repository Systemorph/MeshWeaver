using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The node menu is projected for the VIEWER the layout area was opened for — never for whatever
/// identity happens to be ambient on the turn that re-renders it (MeshWeaver.Plugins#2784).
///
/// <para>Measured before the fix: on a USER partition root the ⋯ menu was folded from the SYSTEM
/// identity's effective permissions and its viewer-scoped entries were built for
/// <c>system-security</c>, while the person who opened the page was someone else. The render that
/// produced it ran on a mesh-query emission whose turn carried the system identity, and the
/// providers read the ambient identity there. The menu is an OFFER — every action re-checks on the
/// server — but an offer computed for the wrong principal is wrong in both directions: a read-only
/// viewer is offered Update-gated entries, and the owner is offered "Pin" on their own home.</para>
///
/// <para>Pin and Versions are rendered by the optional DefaultViews package, which this core mesh
/// does not carry, so the menu would drop them as unrenderable (#3604) and every assertion about
/// them would pass on nothing. They are registered here the way the package registers them.</para>
/// </summary>
public class NodeMenuViewerIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Owner = "menu-owner";
    private const string Reader = "menu-reader";
    private const string Space = "MenuSpace";
    private const string Page = Space + "/Page";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout => layout
                .WithView(PinLayoutArea.PinArea,
                    (LayoutAreaHost _, RenderingContext _) =>
                        Observable.Return((UiControl?)Controls.Markdown("pin")))
                .WithView(MeshNodeLayoutAreas.VersionsArea,
                    (LayoutAreaHost _, RenderingContext _) =>
                        Observable.Return((UiControl?)Controls.Markdown("versions")))))
            .AddMeshNodes(
                new MeshNode(Owner)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Menu Owner",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Menu Owner", Email = $"{Owner}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Owner, "Admin", Owner),
                new MeshNode(Reader)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Menu Reader",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Menu Reader", Email = $"{Reader}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Reader, "Admin", Reader),
                // The reader may READ the owner's home — and nothing more.
                AssignmentNodeFactory.UserRole(Reader, "Viewer", Owner),
                new MeshNode(Space) { Name = "Menu Space", NodeType = "Space" },
                new MeshNode("Page", Space) { Name = "A Page" },
                AssignmentNodeFactory.UserRole(Owner, "Admin", Space),
                AssignmentNodeFactory.UserRole(Reader, "Viewer", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private IObservable<IReadOnlyList<NodeMenuItemDefinition>> NodeMenu(string viewer, string address)
    {
        TestUsers.DevLogin(Mesh, new AccessContext
        {
            ObjectId = viewer, Name = viewer, Email = $"{viewer}@meshweaver.io",
        });
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(address), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .OfType<MenuControl>()
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)x.Items);
    }

    /// <summary>The menu once it carries <paramref name="area"/> — the control that the menu has
    /// rendered its permission-gated slice, so an absence asserted afterwards is a verdict.</summary>
    private Task<IReadOnlyList<NodeMenuItemDefinition>> MenuCarrying(string viewer, string address, string area, CancellationToken ct)
        => NodeMenu(viewer, address)
            .Where(i => i.Any(m => m.Area == area))
            .FirstAsync()
            .Timeout(Budget)
            .Await(ct);

    /// <summary>
    /// A viewer holding only Read on someone's home is offered nothing that needs Update, and Pin
    /// is built for THEM (offered — it is not their home). Under the system identity the fold reads
    /// <c>All</c>, so Recycle (Update-gated) appeared.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AReadOnlyViewer_OfAUserRoot_IsOfferedNoUpdateGatedEntry()
    {
        var items = await MenuCarrying(Reader, Owner, MeshNodeLayoutAreas.VersionsArea, TestContext.Current.CancellationToken);

        items.Should().NotContain(m => m.Area == MeshNodeLayoutAreas.RecycleArea,
            "the reader holds Read only; Recycle needs Update, so the menu must not offer it");
        items.Should().NotContain(m => m.Area == MeshNodeLayoutAreas.SettingsArea,
            "another person's settings page refuses this viewer");
        items.Should().Contain(m => m.Area == PinLayoutArea.PinArea,
            "Pin is viewer-scoped and offered on a home that is not the viewer's");
    }

    /// <summary>The owner's own home never offers Pin — the viewer IS the page. Built for any
    /// other principal (system-security), Pin appears.</summary>
    [Fact(Timeout = 60000)]
    public async Task TheOwner_OfTheirOwnRoot_IsNotOfferedPin()
    {
        var items = await MenuCarrying(Owner, Owner, MeshNodeLayoutAreas.RecycleArea, TestContext.Current.CancellationToken);

        items.Should().NotContain(m => m.Area == PinLayoutArea.PinArea,
            "Pin is built for the viewer, and the owner does not pin their own home");
        items.Select(m => m.Area).Should().Contain(MeshNodeLayoutAreas.SettingsArea,
            "the owner's ⋯ opens their own person app");
    }

    /// <summary>The same read-only viewer on a Space child — the non-user-root shape.</summary>
    [Fact(Timeout = 60000)]
    public async Task AReadOnlyViewer_OfASpaceChild_IsOfferedNoUpdateGatedEntry()
    {
        var items = await MenuCarrying(Reader, Page, MeshNodeLayoutAreas.VersionsArea, TestContext.Current.CancellationToken);

        items.Should().NotContain(m => m.Area == MeshNodeLayoutAreas.RecycleArea,
            "the reader holds Read only on the Space");
        items.Should().Contain(m => m.Area == PinLayoutArea.PinArea,
            "Pin is offered to any signed-in viewer of a node that is not their home");
    }
}
