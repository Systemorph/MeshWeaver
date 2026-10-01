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
                HubConfiguration = config => config.AddMeshDataSource(s => s.WithContentType<TemplateGadget>()),
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

        var bound = Bound(stream, header.DataContext!, "title");
        await bound.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo One",
            "the projection carries the node's name");

        await Mesh.GetMeshNodeStream(GadgetPath).Update(n => n with { Name = "Gizmo Renamed" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        await bound.Should().Within(TestTimeouts.Convergence).Match(t => t == "Gizmo Renamed",
            "the page is not rebuilt — the bound title follows the edit");

        var meta = Bound(stream, header.DataContext!, "metaHtml");
        await meta.Should().Within(TestTimeouts.Convergence).Match(
            t => t != null && t.Contains("Type:") && t.Contains(GadgetType),
            "the provenance line is computed on the hub and bound into the NodeMeta row");
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
        await Bound(stream, body.DataContext!, "style").Should().Within(TestTimeouts.Convergence)
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

        await Bound(stream, strip.DataContext!, "metaHtml").Should().Within(TestTimeouts.Convergence).Match(
            t => t != null && t.Contains("Type:") && t.Contains(ProvenanceType),
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
        => (T)(await Walk(stream, area).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is T t && predicate(t), because))!;

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

    /// <summary>The live text at <paramref name="field"/> of a projection bound at
    /// <paramref name="dataContext"/>, as the GUI resolves it.</summary>
    private static IObservable<string?> Bound(ISynchronizationStream<JsonElement> stream, string dataContext, string field)
        => stream.GetDataStream<JsonElement>(new JsonPointerReference($"{dataContext}/{field}"))
            .Select(je => je.ValueKind == JsonValueKind.String ? je.GetString() : null)
            .Replay(1)
            .RefCount();
}
