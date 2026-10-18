using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
/// The default node page — <c>Overview</c> / <c>Data</c> (header, property overview, markdown
/// body), the provenance strip a framework-composed landing page carries, and <c>Edit</c> — is a
/// TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later", B1 part 2): the page is emitted
/// without waiting on the node and BINDS what it shows.
///
/// <para><b>What each used to do.</b> All four waited for the node (Overview and Data also for the
/// partition root), then built every control out of the node's values: the title as an
/// <c>&lt;h1&gt;</c> carrying the name, the provenance line as HTML carrying the stamps, the markdown
/// body carrying the text. The page showed the loading placeholder until the node's owning hub
/// answered, and every edit rebuilt the whole page.</para>
///
/// <para><b>The assertion is the POINTER.</b> A control that carries the text itself — the
/// pre-conversion shape — never matches the waits below, so a regression times out and reports
/// what it saw. The bound value is then resolved the way the GUI resolves it (the area's own
/// <c>/data</c>, or the node for a node-bound context) and must follow a later edit.</para>
///
/// <para><b>Negative control</b>, run before this file was committed: against the pre-conversion
/// <c>MeshNodeLayoutAreas</c> / <c>OverviewLayoutArea</c> (B1 part 1's head) all five tests fail,
/// each timing out on its first wait.</para>
/// </summary>
public class NodePageIsATemplateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string GadgetType = "TplGadget";
    private const string Space = "TplTwo";
    private const string GadgetPath = "TplTwo/Gizmo";
    private const string UntypedPath = "TplTwo/Loose";
    private const string ProvenanceType = "TplLanding";
    private const string LandingPath = "TplTwo/Landing";

    /// <summary>The content type the gadget NodeType is configured with.</summary>
    public record TemplateGadget
    {
        /// <summary>A free text field.</summary>
        public string? Colour { get; init; }
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(GadgetType)
            {
                Name = "Gadget",
                // Metadata is not a default area: a type opts in (the AI engine's ThreadMessage does).
                HubConfiguration = config => config.AddMeshDataSource(s => s.WithContentType<TemplateGadget>())
                    .AddLayout(layout => layout.WithView(MeshNodeLayoutAreas.MetadataArea, MeshNodeLayoutAreas.Metadata)),
            },
            new MeshNode(ProvenanceType)
            {
                Name = "Landing",
                HubConfiguration = config => config.AddLayout(layout =>
                    layout.WithNodePage(MeshNodeLayoutAreas.OverviewArea, (_, _) =>
                        Observable.Return<UiControl?>(Controls.Markdown("the landing page's own body")))),
            },
            new MeshNode(Space) { NodeType = "Space", Name = Space },
            new MeshNode("Gizmo", Space)
            {
                Name = "Gizmo One",
                NodeType = GadgetType,
                Content = new TemplateGadget { Colour = "TEAL" },
            },
            new MeshNode("Loose", Space) { Name = "Loose" },
            new MeshNode("Landing", Space) { Name = "Landing", NodeType = ProvenanceType });

    [Fact]
    public async Task TheOverviewHeader_IsBound_AndFollowsARename()
    {
        var stream = Open(GadgetPath, MeshNodeLayoutAreas.OverviewArea);
        var header = await Find<StackControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.HeaderDataId) == true,
            "the header is a template bound to its projection in /data — never a tree built from the node");
        var title = await Find<LabelControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            l => l.Data is JsonPointerReference { Pointer: "title" },
            "the title is a POINTER into the header's projection, not the name baked in on the hub");
        title.Should().NotBeNull();

        var bound = Bound(stream, ContextOf(header), "title");
        await bound.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo One",
            "the projection carries the node's name");

        await Mesh.GetMeshNodeStream(GadgetPath).Update(n => n with { Name = "Gizmo Renamed" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await bound.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo Renamed",
            "the page is not rebuilt — the bound title follows the edit");

        await Bound(stream, ContextOf(header), "meta/typeText").Should().Within(TestTimeouts.Convergence)
            .Match(t => t == GadgetType, "the provenance line is computed on the hub and bound into the NodeMeta row");
        await Bound(stream, ContextOf(header), "meta/typeHref").Should().Within(TestTimeouts.Convergence)
            .Match(t => t != null && t.Contains(GadgetType), "the type segment links to the type's configuration");
    }

    /// <summary>
    /// The header's icon tile and provenance line are PLATFORM CONTROLS bound by pointer — an
    /// <see cref="ImageControl"/> whose source is the projection's <c>icon</c>, a <see cref="LinkControl"/>
    /// whose text and href are the type segment's — never an <see cref="HtmlControl"/> carrying markup
    /// computed on the hub. Before this change the tile was an <c>IconControl</c> and the whole line
    /// one bound <c>HtmlControl</c> (<c>metaHtml</c>), so both waits below time out against it.
    /// </summary>
    [Fact]
    public async Task TheHeaderIconAndProvenance_ArePlatformControlsBoundByPointer()
    {
        var stream = Open(GadgetPath, MeshNodeLayoutAreas.OverviewArea);
        var header = await Find<StackControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.HeaderDataId) == true,
            "the header is a template bound to its projection");

        await Find<ImageControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            i => i.Source is JsonPointerReference { Pointer: "icon" },
            "the icon tile is an ImageControl bound to the projected icon");
        await Find<LinkControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            l => l.Text is JsonPointerReference { Pointer: "meta/typeText" }
                 && l.Href is JsonPointerReference { Pointer: "meta/typeHref" },
            "the type segment is a Link bound to the projected type");
        await Bound(stream, ContextOf(header), "icon").Should().Within(TestTimeouts.Convergence)
            .Match(t => !string.IsNullOrEmpty(t), "the projection carries an icon source (the node's, its type's, or the placeholder)");
    }

    /// <summary>
    /// The Metadata page is a template too: a key/value list of Labels and Links bound to a
    /// projection of the node, following a rename in place. It used to wait for the node and build
    /// <c>&lt;p&gt;&lt;strong&gt;Name:&lt;/strong&gt; …&lt;/p&gt;</c> HTML out of its values.
    /// </summary>
    [Fact]
    public async Task TheMetadataPage_IsBound_AndFollowsARename()
    {
        var stream = Open(GadgetPath, MeshNodeLayoutAreas.MetadataArea);
        var page = await Find<StackControl>(stream, MeshNodeLayoutAreas.MetadataArea,
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.MetadataDataId) == true,
            "the Metadata page is a template bound to its projection in /data");
        await Find<LabelControl>(stream, MeshNodeLayoutAreas.MetadataArea,
            l => l.Data is JsonPointerReference { Pointer: "name" },
            "the name is a pointer, not the value baked in on the hub");

        var name = Bound(stream, ContextOf(page), "name");
        await name.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo One", "the projection carries the name");
        await Bound(stream, ContextOf(page), "parentHref").Should().Within(TestTimeouts.Convergence)
            .Match(t => t == $"/{Space}/{MeshNodeLayoutAreas.OverviewArea}", "the parent row links to the parent");

        await Mesh.GetMeshNodeStream(GadgetPath).Update(n => n with { Name = "Gizmo Metadata" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await name.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo Metadata",
            "the bound name follows the edit");
    }

    [Fact]
    public async Task TheOverviewForm_IsChosenFromConfiguration_AndBindsTheNode()
    {
        var stream = Open(GadgetPath, MeshNodeLayoutAreas.OverviewArea);
        var nodeContext = LayoutAreaReference.GetMeshNodeDataContext(GadgetPath, bindContent: true);
        await Find<UiControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            c => c.DataContext == nodeContext,
            "the property form is the configured content type's, its fields bound to the node itself");

        var body = await Find<MarkdownControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            m => m.Markdown is JsonPointerReference,
            "the markdown body is bound to a projection of the node, never its text baked in");
        await Bound(stream, ContextOf(body), "style").Should().Within(TestTimeouts.Convergence)
            .Match(s => s != null && s.Contains("display: none"),
                "a node with no markdown body hides the bound body instead of omitting the control");
    }

    [Fact]
    public async Task AHubWithNoContentType_RendersItsFormInASkeletonSlot()
    {
        var stream = Open(UntypedPath, MeshNodeLayoutAreas.OverviewArea);
        var slot = await Find<LayoutAreaControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            c => c.Reference.Area == OverviewLayoutArea.ContentFormArea,
            "with no configured content type, the form's shape needs the node — in its own slot");
        slot.SpinnerType.Should().Be(SpinnerType.Skeleton, "the slot shows the loading shape, the page does not wait");
        slot.Reference.Id?.ToString().Should().Be(OverviewLayoutArea.ContentFormOverview);
    }

    [Fact]
    public async Task TheProvenanceStrip_IsBound()
    {
        var stream = Open(LandingPath, MeshNodeLayoutAreas.OverviewArea);
        var strip = await Find<StackControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.ProvenanceDataId) == true,
            "the composed provenance strip is a template bound to its projection");

        await Bound(stream, ContextOf(strip), "meta/typeText").Should().Within(TestTimeouts.Convergence).Match(
            t => t == ProvenanceType,
            "the provenance line is computed on the hub and bound into the strip");
        await Find<MarkdownControl>(stream, MeshNodeLayoutAreas.OverviewArea,
            m => m.Markdown?.ToString() == "the landing page's own body",
            "the page itself is composed below the strip, unchanged");
    }

    [Fact]
    public async Task TheEditPage_IsATemplate_WithItsFormBoundToTheNode()
    {
        var stream = Open(GadgetPath, MeshNodeLayoutAreas.EditArea);
        await Find<StackControl>(stream, MeshNodeLayoutAreas.EditArea,
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.HeaderDataId) == true,
            "the Edit page's header is the same bound template");
        var nodeContext = LayoutAreaReference.GetMeshNodeDataContext(GadgetPath, bindContent: true);
        await Find<UiControl>(stream, MeshNodeLayoutAreas.EditArea,
            c => c.DataContext == nodeContext,
            "every field of the Edit form writes straight back to the node");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

    private ISynchronizationStream<JsonElement> Open(string path, string area)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(path), new LayoutAreaReference(area));

    /// <summary>The first control of type <typeparamref name="T"/> matching <paramref name="predicate"/>
    /// anywhere in the area's rendered tree.</summary>
    private static async Task<T> Find<T>(
        ISynchronizationStream<JsonElement> stream, string area, Func<T, bool> predicate, string because)
        where T : UiControl
        => await stream.WalkTree(area).OfType<T>().Should().Within(TestTimeouts.Convergence)
            .Match(predicate, because);

    /// <summary>The data context a bound template carries — the test fails, naming the control, when
    /// it carries none.</summary>
    private static string ContextOf(UiControl control)
        => control.DataContext
           ?? throw new InvalidOperationException($"{control.GetType().Name} carries no DataContext to resolve a bound value against.");

    /// <summary>The live text at <paramref name="field"/> of a projection bound at
    /// <paramref name="dataContext"/>, as the GUI resolves it.</summary>
    private static IObservable<string?> Bound(ISynchronizationStream<JsonElement> stream, string dataContext, string field)
        => stream.GetDataStream<JsonElement>(new JsonPointerReference($"{dataContext}/{field}"))
            .Select(je => je.ValueKind == JsonValueKind.String ? je.GetString() : null)
            .Replay(1)
            .RefCount();
}
