using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The node header's object actions: <b>Edit</b> as the one primary button and a labelled
/// <b>⋯ More</b> dropdown carrying the rest of the node menu — the GitHub / Fluent shape that
/// replaced the unlabelled top-bar cube as the way into a node's operations. These pin the pure arrangement; <see cref="NodeHeaderMoreActionsRenderTest"/>
/// pins that the live header actually carries it.
/// </summary>
public class NodeHeaderMoreActionsTest
{
    private const string Node = "acme/Report";

    private static NodeMenuItemDefinition Item(string area, int order, string? href = null)
        => new(area, area, Order: order, Href: href ?? MeshNodeLayoutAreas.BuildUrl(Node, area));

    private static readonly NodeMenuItemDefinition Divider = new("", NodeMenuItemDefinition.SeparatorArea);

    private static IReadOnlyList<NodeMenuItemDefinition> TypicalMenu =>
    [
        Item(MeshNodeLayoutAreas.EditArea, 10),
        Item("Pin", 12),
        Item(MeshNodeLayoutAreas.MoveArea, 14),
        Item(MeshNodeLayoutAreas.CopyArea, 16),
        Item(MeshNodeLayoutAreas.DeleteArea, 18),
        Divider,
        Item(MeshNodeLayoutAreas.FilesArea, 30),
        Item("RequestSignature", 31, "/Signature/Workspace/RequestSignature?doc=acme/Report"),
        Divider,
        new NodeMenuItemDefinition("Recycle", MeshNodeLayoutAreas.RecycleArea, Order: 50, Href: "/acme/Report")
            { Action = MenuActions.Recycle },
    ];

    private static string[] Areas(IEnumerable<NodeMenuItemDefinition> items) => items.Select(i => i.Area).ToArray();

    /// <summary>Destructive last: Delete leaves its band and closes the list, behind its own divider.</summary>
    [Fact]
    public void DeleteMovesToTheEndBehindADivider()
    {
        var arranged = MeshNodeLayoutAreas.ArrangeMoreActions(TypicalMenu, editShown: true);

        Assert.Equal(MeshNodeLayoutAreas.DeleteArea, arranged[^1].Area);
        Assert.Equal(NodeMenuItemDefinition.SeparatorArea, arranged[^2].Area);
        Assert.Single(arranged, i => i.Area == MeshNodeLayoutAreas.DeleteArea);
    }

    /// <summary>The header already shows Edit as a button, so ⋯ does not offer it twice…</summary>
    [Fact]
    public void EditIsNotRepeatedWhenTheHeaderShowsIt()
        => Assert.DoesNotContain(MeshNodeLayoutAreas.EditArea,
            Areas(MeshNodeLayoutAreas.ArrangeMoreActions(TypicalMenu, editShown: true)));

    /// <summary>…but where the header has no Edit button (a Markdown page edits inline), ⋯ keeps it.</summary>
    [Fact]
    public void EditStaysWhenTheHeaderDoesNotShowIt()
        => Assert.Contains(MeshNodeLayoutAreas.EditArea,
            Areas(MeshNodeLayoutAreas.ArrangeMoreActions(TypicalMenu, editShown: false)));

    /// <summary>A contributed entry (the e-Signature package's) rides through untouched.</summary>
    [Fact]
    public void ContributedEntriesAreKept()
        => Assert.Contains("RequestSignature",
            Areas(MeshNodeLayoutAreas.ArrangeMoreActions(TypicalMenu, editShown: true)));

    /// <summary>Removing entries never strands a divider at either end or doubles one.</summary>
    [Fact]
    public void NoDividerLeadsTrailsOrDoubles()
    {
        IReadOnlyList<NodeMenuItemDefinition> menu =
        [
            Item(MeshNodeLayoutAreas.EditArea, 10), Divider, Divider, Item(MeshNodeLayoutAreas.FilesArea, 30), Divider,
        ];
        var arranged = MeshNodeLayoutAreas.ArrangeMoreActions(menu, editShown: true);

        Assert.Equal([MeshNodeLayoutAreas.FilesArea], Areas(arranged));
    }

    /// <summary>A menu holding only Delete is just Delete — no divider in front of nothing.</summary>
    [Fact]
    public void DeleteAloneHasNoDivider()
        => Assert.Equal([MeshNodeLayoutAreas.DeleteArea],
            Areas(MeshNodeLayoutAreas.ArrangeMoreActions([Item(MeshNodeLayoutAreas.DeleteArea, 18)], editShown: true)));

    /// <summary>Nothing to offer ⇒ no ⋯ at all, never an empty dropdown.</summary>
    [Fact]
    public void AnEmptyMenuRendersNoTrigger()
    {
        var control = MeshNodeLayoutAreas.BuildMoreActions([Divider], Node, "More");
        Assert.Empty(Assert.IsType<StackControl>(control).Areas);
    }

    /// <summary>The trigger is the platform MenuItem control, labelled, with the stable class tests hook.</summary>
    [Fact]
    public void TheTriggerIsALabelledPlatformMenu()
    {
        var control = MeshNodeLayoutAreas.BuildMoreActions(
            MeshNodeLayoutAreas.ArrangeMoreActions(TypicalMenu, editShown: true), Node, "More");

        var menu = Assert.IsType<MenuItemControl>(control);
        Assert.Equal("More", menu.Title);
        Assert.Equal(MeshNodeLayoutAreas.MoreActionsClass, menu.Class);
        Assert.NotEmpty(menu.Areas);
    }

    /// <summary>
    /// 🚨 Recycle must run on the CIRCUIT (#2202) — the dropdown lives on the hub it tears down —
    /// so its entry navigates to the /{path}/Recycle URL the portal intercepts, not to the landing
    /// href the top bar uses as its after-action destination.
    /// </summary>
    [Fact]
    public void RecycleNavigatesToTheUrlThePortalRunsOnTheCircuit()
        => Assert.Equal("/acme/Report/Recycle",
            MeshNodeLayoutAreas.MoreActionHref(TypicalMenu[^1], Node));

    /// <summary>An entry that names its own href keeps it — a package's front door is not rewritten.</summary>
    [Fact]
    public void AnExplicitHrefIsFollowed()
        => Assert.Equal("/Signature/Workspace/RequestSignature?doc=acme/Report",
            MeshNodeLayoutAreas.MoreActionHref(TypicalMenu[7], Node));
}

/// <summary>
/// End to end through the layout client: the Overview's header carries the ⋯ More dropdown, read
/// from the SAME <c>$Menu:Node</c> list the top bar reads — so the platform's own Recycle entry is
/// in it, pointing at the circuit-run recycle URL.
/// </summary>
public class NodeHeaderMoreActionsRenderTest(ITestOutputHelper output)
    : NodeMenuRenderableAreaTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    [HubFact]
    public async Task TheHeaderCarriesTheMoreMenuWithTheNodeMenusEntries()
    {
        // The menu must be up (and the viewer entitled) before the header can mirror it.
        await RenderedMenu();

        var client = GetClient();
        var options = client.JsonSerializerOptions;
        var recycleHref = MeshNodeLayoutAreas.BuildUrl(Node, MeshNodeLayoutAreas.RecycleArea);

        // Class and href arrive over the wire as JSON values, so they compare as TEXT.
        // The last snapshot seen is kept so a timeout says WHAT the header held instead of just "timed out".
        IReadOnlyDictionary<string, UiControl> last = new Dictionary<string, UiControl>();
        IReadOnlyDictionary<string, UiControl> found;
        try
        {
            found = await client.GetWorkspace()
                .GetRemoteStream<JsonElement, LayoutAreaReference>(
                    new Address(Node), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
                .Select(store => last = Controls(store, options))
                .Where(controls =>
                    controls.Any(c => c.Key.EndsWith("/" + MeshNodeLayoutAreas.NodeActionsArea)
                                      && c.Value is MenuItemControl menu
                                      && menu.Class?.ToString() == MeshNodeLayoutAreas.MoreActionsClass)
                    && controls.Values.OfType<ButtonControl>().Any(b => b.NavigateToHref?.ToString() == recycleHref))
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await();
        }
        catch (TimeoutException)
        {
            Assert.Fail("The header never carried ⋯ More with Recycle. Areas seen: "
                        + string.Join(", ", last.Select(c => $"{c.Key}={c.Value.GetType().Name}{(c.Value is ButtonControl b ? "->" + b.NavigateToHref : "")}")));
            throw;
        }

        Assert.NotEmpty(found);
    }

    private static IReadOnlyDictionary<string, UiControl> Controls(ChangeItem<JsonElement> store, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, UiControl>();
        if (store.Value.ValueKind != JsonValueKind.Object
            || !store.Value.TryGetProperty(LayoutAreaReference.Areas, out var areas)
            || areas.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var area in areas.EnumerateObject())
        {
            if (area.Value.ValueKind != JsonValueKind.Object)
                continue;
            try
            {
                if (area.Value.Deserialize<UiControl>(options) is { } control)
                    result[area.Name.Trim('"')] = control;
            }
            catch (JsonException) { /* a control type this client does not register — not ours */ }
            catch (NotSupportedException) { }
        }
        return result;
    }
}
