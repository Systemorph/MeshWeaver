using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using static MeshWeaver.Graph.Test.MarkdownEditIsATemplateTest;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The default node page's secondary areas are TEMPLATES (Doc/GUI/DataBinding → "Templates first,
/// data later"): each emits its whole control tree without waiting on the node, and binds what it
/// shows — by node path (a card, a title), by query (a list the GUI runs), or by a projection fed
/// into <c>/data</c> (a serialization the hub computes).
///
/// <para><b>What each used to do.</b> <c>Thumbnail</c> (default and Markdown), <c>NodeTypes</c>,
/// the <c>Notebook</c> page, and the self-references <c>$Data</c> / <c>$Schema</c> /
/// <c>$Content</c> all subscribed to the node on the hub, waited for it, and then built their
/// controls out of its values: the slot showed the loading placeholder until the node's owning hub
/// answered, and what it finally showed was that moment's snapshot. <c>Search</c> did the same to
/// choose between two catalogs — on every node, although only a NodeType DEFINITION needs the
/// read.</para>
///
/// <para><b>Not asserted here: <c>$Data</c>.</b> <see cref="MeshNodeLayoutAreas.Data"/>'s
/// self-reference is converted the same way as <c>$Content</c>, but on a node hub the layout's
/// standard <c>DataPathViews</c> predicate renderer also matches <c>$Data</c>, runs AFTER the named
/// renderer and overwrites its control — so what a client sees there is <c>DataPathViews</c>'
/// output, which is outside this change.</para>
///
/// <para><b>Negative control</b>, run before this file was committed: against the pre-conversion
/// <c>ContentLayoutArea</c>, the <c>$Content</c> arm times out on its first wait — every control
/// it saw carried the text itself, never a pointer.</para>
/// </summary>
public class NodePageAreasAreTemplatesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Space = "Tpl";
    private const string PagePath = "Tpl/Page";
    private const string TypePath = "Tpl/Widget";
    private const string Marker = "TEMPLATE_MARKER_ONE";

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient()
            .WithType(typeof(MeshNodeThumbnailControl), nameof(MeshNodeThumbnailControl))
            .WithType(typeof(NotebookControl), nameof(NotebookControl))
            .WithType(typeof(NotebookCellControl), nameof(NotebookCellControl))
            .WithType(typeof(NotebookSkin), nameof(NotebookSkin));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(Space) { NodeType = "Space", Name = Space },
            new MeshNode("Page", Space)
            {
                Name = "Page",
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = $"# Page\n\n{Marker}\n\n```csharp\nvar x = 1;\n```\n" },
            },
            new MeshNode("Widget", Space)
            {
                Name = "Widget",
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition { Description = "A widget type" },
            });

    // ── the pure templates ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AThumbnailByPath_CarriesOnlyThePath()
    {
        var card = MeshNodeThumbnailControl.ForPath("Tpl/Docs/Readme");

        card.NodePath.Should().Be("Tpl/Docs/Readme");
        card.Title.Should().Be("Readme", "until the node answers, the card names the path's last segment");
        card.Description.Should().BeNull("nothing is read on the hub — the card's view binds the node");
        card.ImageUrl.Should().BeNull();
    }

    [Fact]
    public void TheNodeTypesPage_IsATemplate_ThatListsByQuery()
    {
        var page = MeshNodeLayoutAreas.NodeTypesTemplate(PagePath, "Markdown", locale: "en");

        EveryViewIsStatic(page);
        Descendants(page).OfType<MeshNodeThumbnailControl>().Should().ContainSingle(
                "the node's own type is a card bound by PATH")
            .Which.NodePath.Should().Be("Markdown");
        var list = Descendants(page).OfType<MeshSearchControl>().Should().ContainSingle(
            "the NodeTypes at this level are a query the GUI runs — never a snapshot taken on the hub").Subject;
        list.HiddenQuery!.ToString().Should().Contain($"namespace:{PagePath}")
            .And.Contain($"nodeType:{MeshNode.NodeTypePath}")
            .And.Contain("-path:Markdown", "the own type is the card above — the list must not repeat it");

        Descendants(MeshNodeLayoutAreas.NodeTypesTemplate(PagePath, null, "en"))
            .OfType<MeshNodeThumbnailControl>().Should().BeEmpty("a node without a type shows no own-type card");
        MeshNodeLayoutAreas.NodeTypesAtLevelQuery(PagePath, null).Should().NotContain("-path:",
            "with no own type there is nothing to exclude");
    }

    [Fact]
    public void TheSelfReferenceIcon_EncodesTheNodesIconUrl()
    {
        var markup = MeshNodeLayoutAreas.RenderNodeIconHtml(
            new MeshNode("Page", "Space") { Icon = "/x.png\" onerror=\"alert(1)", Name = "<b>n</b>" });

        markup.Should().NotContain("\" onerror=",
            "the icon URL is editable node content — a quote in it must not break out of src=\"…\"");
        markup.Should().Contain("&quot; onerror=");
        markup.Should().NotContain("<b>n</b>", "the name is encoded as well");
    }

    [Fact]
    public void TheNotebookPage_IsATemplate_TitleBoundAndCellsInASkeletonSlot()
    {
        var page = MarkdownNotebookLayoutArea.BuildTemplate(PagePath);

        EveryViewIsStatic(page);
        var title = Descendants(page).OfType<LabelControl>().Should().ContainSingle().Subject;
        title.Data.Should().BeOfType<JsonPointerReference>(
            "the title is the node's Name read by the GUI, not a string baked in on the hub")
            .Which.Pointer.Should().Be(nameof(MeshNode.Name));
        title.DataContext.Should().Be(LayoutAreaReference.GetMeshNodeDataContext(PagePath, bindContent: false));

        var cells = Descendants(page).OfType<LayoutAreaControl>().Should().ContainSingle(
            "the cells — the one part that must be parsed — render into their own slot").Subject;
        cells.Reference.Area.Should().Be(MarkdownNotebookLayoutArea.CellsArea);
        cells.SpinnerType.Should().Be(SpinnerType.Skeleton, "the slot shows the loading shape, not a spinner");
    }

    [Fact]
    public void WhichCatalogSearchShows_IsReadFromTheConfiguration()
    {
        var plain = new MessageHubConfiguration(null, new Address("probe", "x"));
        MeshNodeLayoutAreas.IsNodeTypeCatalog(plain).Should().BeFalse();
        MeshNodeLayoutAreas.IsNodeTypeCatalog(plain.WithNodeTypePath("Markdown")).Should().BeFalse(
            "an instance of a type shows the ordinary catalog");
        MeshNodeLayoutAreas.IsNodeTypeCatalog(plain.WithNodeTypePath(MeshNode.NodeTypePath)).Should().BeTrue(
            "a NodeType DEFINITION's hub applies the 'NodeType' type — it catalogs its instances");
        MeshNodeLayoutAreas.IsNodeTypeCatalog(plain.Set(new NodeTypeCatalogMode())).Should().BeTrue();
    }

    // ── the areas, rendered on a live node ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMarkdownThumbnailArea_IsThePathOnlyCard()
    {
        var (stream, area) = Open(PagePath, MeshNodeLayoutAreas.ThumbnailArea);
        var card = await stream.GetControlStream(area).OfType<MeshNodeThumbnailControl>()
            .Should().Within(TestTimeouts.Convergence).Emit();

        card.NodePath.Should().Be(PagePath);
        card.Description.Should().BeNull("the area no longer reads the node to fill the card in");
    }

    [Fact]
    public async Task TheContentSelfReference_IsBound_AndFollowsAnEdit()
    {
        var (stream, area) = Open(PagePath, ContentCollectionsExtensions.ContentAreaName);
        var markdown = await BoundMarkdown(stream, area);
        var bound = BoundText(stream, markdown, markdown.Markdown);

        await bound.Should().Within(TestTimeouts.Convergence).Match(
            t => t != null && t.Contains(Marker), "the node's own markdown is projected into the slot");

        await Edit("TEMPLATE_MARKER_THREE");
        await bound.Should().Within(TestTimeouts.Convergence).Match(
            t => t != null && t.Contains("TEMPLATE_MARKER_THREE"), "and follows a later edit");
    }

    [Fact]
    public async Task TheSchemaSelfReference_NamesTheConfiguredContentType()
    {
        var (stream, area) = Open(PagePath, MeshNodeLayoutAreas.SchemaArea);
        var markdown = await stream.GetControlStream(area).OfType<MarkdownControl>()
            .Should().Within(TestTimeouts.Convergence).Emit();

        var text = markdown.Markdown.ToString();
        text.Should().Contain("### MeshNode");
        text.Should().Contain($"Content Type: {nameof(MarkdownContent)}",
            "the content type is the one the hub's MeshDataSource was configured with — no read needed");
    }

    [Fact]
    public async Task TheNotebookCells_AreParsedFromTheNodesMarkdown()
    {
        var (stream, area) = Open(PagePath, MarkdownNotebookLayoutArea.CellsArea);
        var notebook = await stream.GetControlStream(area).OfType<NotebookControl>()
            .Should().Within(TestTimeouts.Convergence).Emit();

        notebook.Cells.Should().HaveCountGreaterThanOrEqualTo(2, "a markdown cell and the csharp cell");
    }

    [Fact]
    public async Task TheNotebookTitle_ReadsTheName_AndFollowsARename()
    {
        var title = Descendants(MarkdownNotebookLayoutArea.BuildTemplate(PagePath)).OfType<LabelControl>().Single();
        var ctx = LayoutAreaReference.TryParseMeshNodeDataContext(title.DataContext)!.Value;
        var bound = MeshNodeBindingExtensions.Bind(
                Mesh, ctx.NodePath, ctx.BindContent, ctx.SubPath, (JsonPointerReference)title.Data)
            .Replay();
        using var connection = bound.Connect();

        await bound.Should().Within(TestTimeouts.Convergence).Match(v => Text(v) == "Page",
            "the GUI resolves the title's pointer against the node");

        await Mesh.GetMeshNodeStream(PagePath).Update(n => n with { Name = "Renamed" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await bound.Should().Within(TestTimeouts.Convergence).Match(v => Text(v) == "Renamed",
            "the binding stays subscribed, so a rename reaches the open page");
    }

    [Fact]
    public void TheNodeTypeCatalogPage_IsATemplate_TheListInASkeletonSlot()
    {
        var page = MeshNodeLayoutAreas.NodeTypeCatalogTemplate(new Address(TypePath), TypePath, "?groupBy=flat&q=abc");

        EveryViewIsStatic(page);
        Descendants(page).OfType<MeshSearchControl>().Should().BeEmpty(
            "the instance list's query is computed from the definition — it never sits in the frame");
        var slot = Descendants(page).OfType<LayoutAreaControl>().Should().ContainSingle(
            "the one computed part renders into its own slot").Subject;
        slot.Reference.Area.Should().Be(MeshNodeLayoutAreas.NodeTypeInstancesArea);
        slot.Reference.Id.Should().Be("?groupBy=flat&q=abc", "every catalog knob still reaches the list");
        slot.SpinnerType.Should().Be(SpinnerType.Skeleton, "the slot shows the loading shape, not a spinner");
    }

    [Fact]
    public void TheNodeTypeCatalogQuery_IsDecidedByTheDefinition()
    {
        var plain = MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(TypePath, new NodeTypeDefinition());
        plain.HiddenQuery.Should().Be(
            $"namespace:{TypePath} scope:subtree is:main -nodeType:Code -nodeType:NodeType -nodeType:Markdown");
        plain.CreateHref.Should().Be($"/create?type={Uri.EscapeDataString(TypePath)}");

        var grouped = MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(TypePath, new NodeTypeDefinition
        {
            DefaultNamespace = "Tpl/Widgets",
            RestrictedToNamespaces = ["Tpl/A", "Tpl/B"],
        });
        grouped.HiddenQuery.Should().Be($"nodeType:{TypePath} namespace:Tpl/Widgets");
        grouped.CreateHref.Should().Be(
            $"/create?type={Uri.EscapeDataString(TypePath)}&namespace={Uri.EscapeDataString("Tpl/Widgets")}"
            + $"&namespaces={Uri.EscapeDataString("Tpl/A")},{Uri.EscapeDataString("Tpl/B")}");

        MeshNodeLayoutAreas.NodeTypeCatalogQuery.From(TypePath, null).Should().Be(plain,
            "content that is not a definition lists the namespace subtree, as before");
    }

    [Fact]
    public async Task SearchOnADefinition_EmitsTheTemplate_WithoutReadingTheNode()
    {
        var (stream, area) = Open(TypePath, MeshNodeLayoutAreas.SearchArea);
        var slot = Assert.IsType<LayoutAreaControl>(
            await Walk(stream, area).Should().Within(TestTimeouts.Convergence).Match(
                c => c is LayoutAreaControl, "a definition's Search page is the frame with the instance slot"));

        slot.Reference.Area.Should().Be(MeshNodeLayoutAreas.NodeTypeInstancesArea);
    }

    [Fact]
    public async Task TheInstanceSlot_CatalogsTheInstances_AndFollowsTheDefinition()
    {
        var (stream, area) = Open(TypePath, MeshNodeLayoutAreas.NodeTypeInstancesArea);

        await QueryOf(stream, area).Should().Within(TestTimeouts.Convergence).Match(
            q => q != null && q.Contains($"namespace:{TypePath}"),
            "a definition's hub lists its instances by a query the GUI runs");

        await Mesh.GetMeshNodeStream(TypePath)
            .Update(n => n with { Content = new NodeTypeDefinition { Description = "A widget type", DefaultNamespace = "Tpl/Widgets" } })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await QueryOf(stream, area).Should().Within(TestTimeouts.Convergence).Match(
            q => q == $"nodeType:{TypePath} namespace:Tpl/Widgets",
            "the slot follows a later edit of the definition — never a snapshot");
    }

    [Fact]
    public async Task SearchOnAnInstance_IsTheOrdinaryCatalog()
    {
        var (stream, area) = Open(PagePath, MeshNodeLayoutAreas.SearchArea);
        var search = await CatalogOf(stream, area);

        search!.HiddenQuery!.ToString().Should().NotContain("-nodeType:NodeType",
            "an ordinary node shows its content catalog, not a NodeType's instance list");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The catalog the Search area shows, wherever it sits in the rendered tree — the root itself,
    /// under the breadcrumbs, or inside a NodeType's shell.
    /// </summary>
    private static async Task<MeshSearchControl?> CatalogOf(ISynchronizationStream<JsonElement> stream, string area)
        => await Walk(stream, area).Should().Within(TestTimeouts.Convergence).Match(c => c is MeshSearchControl,
            "the Search area shows a catalog") as MeshSearchControl;

    /// <summary>The hidden query of every catalog the area renders, as it changes.</summary>
    private static IObservable<string?> QueryOf(ISynchronizationStream<JsonElement> stream, string area)
        => Walk(stream, area).OfType<MeshSearchControl>().Select(s => s.HiddenQuery?.ToString());

    /// <summary>Every control rendered at <paramref name="area"/> and, recursively, in its sub-areas.</summary>
    private static IObservable<UiControl?> Walk(ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Select(control => control is IContainerControl container
                ? container.Areas
                    .Where(a => a.Area is not null)
                    .Select(a => Walk(stream, a.Area!.ToString()!))
                    .Merge()
                    .StartWith(control)
                : Observable.Return(control))
            .Switch();

    private (ISynchronizationStream<JsonElement> Stream, string Area) Open(string path, string area)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), reference);
        return (stream, reference.Area!);
    }

    /// <summary>
    /// The area's markdown control — past the layout stream's own loading placeholder, which is a
    /// MarkdownControl too. Matching on the POINTER is the assertion: a control carrying the text
    /// itself (the pre-conversion shape) never matches, and the wait reports what it saw.
    /// </summary>
    private static Task<UiControl?> BoundMarkdownCore(ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area).Should().Within(TestTimeouts.Convergence).Match(
            c => c is MarkdownControl { Markdown: JsonPointerReference },
            "the area's markdown is a POINTER into /data — never the text baked in on the hub");

    private static async Task<MarkdownControl> BoundMarkdown(ISynchronizationStream<JsonElement> stream, string area)
        => (MarkdownControl)(await BoundMarkdownCore(stream, area))!;

    /// <summary>The live text behind a projection-bound control's pointer, as the GUI resolves it.</summary>
    private static IObservable<string?> BoundText(
        ISynchronizationStream<JsonElement> stream, UiControl control, object value)
    {
        var pointer = value.Should().BeOfType<JsonPointerReference>(
            "the control carries a POINTER into /data, never the text baked in on the hub").Subject;
        control.DataContext.Should().NotBeNullOrEmpty();
        return stream.GetDataStream<JsonElement>(new JsonPointerReference($"{control.DataContext}/{pointer.Pointer}"))
            .Select(je => Text(je))
            .Replay(1)
            .RefCount();
    }

    private async Task Edit(string marker)
    {
        await Mesh.GetMeshNodeStream(PagePath)
            .Update(n => n with { Content = new MarkdownContent { Content = $"# Page\n\n{marker}\n" } })
            .Should().Within(TestTimeouts.Convergence).Emit();
    }

    private static string? Text(object? emission) =>
        emission is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null;
}
