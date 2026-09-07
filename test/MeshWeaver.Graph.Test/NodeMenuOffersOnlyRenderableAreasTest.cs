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
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Shared plumbing for the two halves of the controlled experiment below — identical mesh,
/// identical node, identical viewer; the ONLY difference between the two concrete classes is
/// whether a renderer for <c>Delete</c> is registered.
/// </summary>
public abstract class NodeMenuRenderableAreaTestBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The node whose menu is read. Plain — no NodeType, so it takes the default chain.</summary>
    protected const string Node = "MenuRendererProbe";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    /// <summary>
    /// The rendered <c>$Menu:Node</c> entries, read through the layout client exactly as the portal
    /// reads them — the assertion is on what a USER is offered, not on what a provider returned.
    /// </summary>
    protected IObservable<IReadOnlyList<NodeMenuItemDefinition>> NodeMenu(Address nodeAddress)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                nodeAddress, new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Where(x => x is MenuControl)
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)((MenuControl)x!).Items);

    /// <summary>
    /// Waits until the node menu has rendered — Edit is a platform-registered entry, so its
    /// presence means "the menu is up and this viewer has rights", never "the assertion is ready to
    /// pass vacuously".
    /// </summary>
    protected async Task<IReadOnlyList<NodeMenuItemDefinition>> RenderedMenu()
        => await NodeMenu(new Address(Node))
            .Where(i => i.Any(m => m.Area == MeshNodeLayoutAreas.EditArea))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);
}

/// <summary>
/// CONTROL half of issue #3604. With a renderer for <c>Delete</c> registered — which is what the
/// optional <c>MeshWeaver.Graph.Views</c> (<c>DefaultViews</c>) package does on a real portal — the
/// Delete entry IS offered.
///
/// <para>🚨 This half is what makes the other half a measurement rather than a tautology. The
/// Delete entry is gated on <c>Permission.Delete</c>, so "Delete is absent" would also be the
/// outcome if the test viewer simply lacked that right, and the experiment would then pass having
/// observed nothing. Same mesh, same node, same viewer, one variable.</para>
/// </summary>
public class NodeMenuOffersDeleteWhenRenderedTest(ITestOutputHelper output)
    : NodeMenuRenderableAreaTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Stands in for the DefaultViews package's node-wide registration.
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.DeleteArea,
                    (LayoutAreaHost _, RenderingContext _) =>
                        Observable.Return((UiControl?)Controls.Markdown("delete confirmation")))))
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    /// <summary>The entry is offered when something on this hub actually renders it.</summary>
    [HubFact]
    public async Task DeleteIsOffered_WhenARendererIsRegistered()
    {
        var items = await RenderedMenu();
        Assert.Contains(items, m => m.Area == MeshNodeLayoutAreas.DeleteArea);
    }
}

/// <summary>
/// Issue #3604 — "The Delete menu item leads to a dead area". The platform assembles the node menu,
/// but the renderers for Delete, Copy, Move, Versions and Pin ship in the OPTIONAL
/// <c>MeshWeaver.Graph.Views</c> (<c>DefaultViews</c>) package. On a mesh without it — this one —
/// every one of those entries used to be offered and every click landed on the layout engine's
/// diagnostic page: <c>"Area not found — No renderer is registered for area `Delete`"</c>.
///
/// <para>Pinned here end to end, through the layout client, because the defect is only visible in
/// the RENDERED menu: the provider produced a perfectly valid entry, and the hub it pointed at
/// simply had nothing to serve it.</para>
/// </summary>
public class NodeMenuHidesUnrenderableAreasTest(ITestOutputHelper output)
    : NodeMenuRenderableAreaTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    /// <summary>
    /// No renderer for Delete anywhere on this mesh ⇒ the entry is not offered. The control class
    /// above proves the viewer holds <c>Permission.Delete</c>, so the absence is attributable to
    /// the missing renderer and to nothing else.
    /// </summary>
    [HubFact]
    public async Task DeleteIsNotOffered_WhenNothingRendersIt()
    {
        var items = await RenderedMenu();
        Assert.DoesNotContain(items, m => m.Area == MeshNodeLayoutAreas.DeleteArea);
    }

    /// <summary>
    /// 🚨 The filter must not become a menu-wide cull. Recycle is an ACTION whose href is the
    /// node's own landing page rather than an area URL, and the platform renders its progress card
    /// itself — it stays, and so does every other entry the platform can actually serve.
    /// </summary>
    [HubFact]
    public async Task PlatformRenderedEntriesSurvive()
    {
        var items = await RenderedMenu();
        Assert.Contains(items, m => m.Area == MeshNodeLayoutAreas.RecycleArea);
        Assert.Contains(items, m => m.Area == MeshNodeLayoutAreas.FilesArea);
    }
}
