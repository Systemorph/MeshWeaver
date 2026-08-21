using System.Text.Json;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// #1636 — <b>ALL code cells must be editable</b>, including the fenced
/// <c>```csharp --render X --show-code</c> workbench inside a markdown BODY, which is what a large
/// share of exercise and lesson workbenches are (every ReinsurancePractice exercise, for one).
/// Editing already worked for Code NODES; the fence stayed a static <c>&lt;pre&gt;</c>, so the one
/// place a learner is asked to write was the one place they could not type.
///
/// <para>These pin the CLIENT half: which cells hydrate into an editor, for whom, and that a
/// read-only viewer's page is byte-for-byte the page they saw before. The persistence half —
/// where the edit lands and that a re-parse finds it — is pinned purely in
/// <c>MarkdownFenceEditingTest</c>, and deliberately comes first (#1606 shipped a Code-node edit
/// that did not survive a reload).</para>
/// </summary>
public class MarkdownCellEditingTest
{
    private const string Workbench =
        "Some prose.\n\n```csharp --render SpotGap --show-code\nvar answer = 0; // TODO\n```\n\nMore prose.\n";

    private const string GivenMaterial =
        "Read this code and say what is wrong with it.\n\n```csharp\nvar broken = 1;\n```\n";

    private static string Html(string markdown) =>
        Markdig.Markdown.ToHtml(markdown, MarkdownExtensions.CreateMarkdownPipeline(null, null));

    /// <summary>
    /// The names of every component the renderer emitted, in document order. Read off the frames
    /// because there is no public API that reports what a RenderTreeBuilder was told to build.
    /// </summary>
    private static List<string> ComponentsOf(string markdown, MarkdownCellEditing? editing)
    {
        var renderer = new MarkdownHtmlRenderer(
            DesignThemeModes.Light, stream: null, runSubmission: null, runState: null, cellEditing: editing);
        var builder = new RenderTreeBuilder();
        renderer.RenderHtml(builder, Html(markdown));

        // BL0006: inspecting render-tree frames is exactly how the sibling tag-safety test pins its
        // invariant — there is no public API for it. Intentional, test-only.
#pragma warning disable BL0006
        var frames = builder.GetFrames();
        var names = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Component)
                names.Add(frame.ComponentType.Name);
        }
        return names;
#pragma warning restore BL0006
    }

    private static MarkdownCellEditing MayWrite(Action<string, string>? onBuffer = null) =>
        new("Course/Lesson/Exercise/SpotTheGap", _ => null, onBuffer);

    [Fact]
    public void ViewerWithUpdate_GetsAnEditorInsteadOfTheStaticPre()
    {
        var components = ComponentsOf(Workbench, MayWrite());

        components.Should().Contain(nameof(MarkdownCodeCellEditor),
            "a viewer who may write the node edits the workbench IN PLACE — the same rule the "
            + "Code-node cell follows, and the whole point of #1636");
        components.Should().NotContain("CodeBlock",
            "the static code block is REPLACED, not rendered beside the editor");
        components.Should().Contain(nameof(MarkdownCodeCellToolbar),
            "the cell keeps its Run bar — an editable cell that cannot be run is half a workbench");
    }

    [Fact]
    public void ViewerWithoutUpdate_KeepsTheReadOnlyRendering()
    {
        // 🚨 The permission decision is the hosting view's, taken server-side from the same
        // evaluator the Code-node cell uses; the renderer is handed the ANSWER. Null is "read-only",
        // and it is the default on every constructor that does not opt in — which is what stops this
        // from widening access as a side effect anywhere it was not deliberately wired.
        var components = ComponentsOf(Workbench, editing: null);

        components.Should().NotContain(nameof(MarkdownCodeCellEditor));
        components.Should().Contain("CodeBlock", "a read-only viewer still SEES the code");
        components.Should().Contain(nameof(MarkdownCodeCellToolbar),
            "and can still run it — running was never gated on Update");
    }

    [Fact]
    public void GivenMaterial_StaysReadOnlyEvenForAViewerWhoMayWrite()
    {
        // A plain fence is given material: legacy code to diagnose, an incident, code under review.
        // It deliberately pre-exists, carries no submission id, and nothing addresses it — so
        // "all code cells editable" must not become "all code editable".
        var components = ComponentsOf(GivenMaterial, MayWrite());

        components.Should().NotContain(nameof(MarkdownCodeCellEditor));
        components.Should().Contain("CodeBlock");
    }

    [Fact]
    public void EveryWorkbenchInADocumentBecomesItsOwnEditor()
    {
        const string two = "```csharp --render First --show-code\nvar a = 1;\n```\n\n"
                           + "```csharp --render Second --show-code\nvar b = 2;\n```\n";

        var components = ComponentsOf(two, MayWrite());

        components.Count(n => n == nameof(MarkdownCodeCellEditor)).Should().Be(2,
            "ALL code cells editable — a page's second workbench is not a lesser one");
    }

    [Fact]
    public void HiddenCodeCellIsNotAnEditor()
    {
        // `--render X` WITHOUT --show-code is a live demo: it streams a result and deliberately shows
        // no source. There is nothing on screen to edit, and materialising an editor for it would put
        // code in front of the reader that the author chose to hide.
        var components = ComponentsOf("```csharp --render Hidden\nControls.Html(\"<b>hi</b>\")\n```", MayWrite());

        components.Should().NotContain(nameof(MarkdownCodeCellEditor));
    }

    [Fact]
    public void TheEditorIsSeededFromTheHostingViewsParse_NotFromReDecodedHtml()
    {
        // The seed has to survive HTML escaping: a generic argument or a comparison in the fence body
        // is written as &lt; on the way in. The hosting view answers from its own parse when it can —
        // this pins that the resolver is consulted and its answer wins.
        var seen = new List<string>();
        var editing = new MarkdownCellEditing(
            "Course/Page",
            id => { seen.Add(id); return "List<int> xs = [];"; },
            null);

        ComponentsOf(Workbench, editing).Should().Contain(nameof(MarkdownCodeCellEditor));
        // The id the renderer asks with is the SUBMISSION id — lower-cased by ParseArguments,
        // which is why every lookup on it is case-insensitive. Asking with the author's `SpotGap`
        // would miss in every consumer that keys on the submission.
        seen.Should().Equal("spotgap");
    }

    [Fact]
    public void EditsRoundTripToWhereTheRunnerReadsThem()
    {
        // The end-to-end claim, stated where it can be proven without a circuit: what the editor
        // saves is what a fresh parse of the stored body hands the kernel under the same id. The
        // component's save is exactly ReplaceFenceBody + MarkdownContent.Parse, so this IS that path.
        const string answer = "var answer = 42;";

        var saved = MarkdownFenceEditing.ReplaceFenceBody(Workbench, "spotgap", answer);
        saved.Should().NotBeNull();

        var stored = MarkdownContent.Parse(saved!);
        stored.CodeSubmissions.Should().NotBeNull();
        stored.CodeSubmissions!.Single(s => s.Id == "spotgap").Code.Trim().Should().Be(answer);

        // …and the page the NEXT reader gets is rendered from that same body, so the editor they
        // open is seeded with the saved answer rather than the original stub.
        stored.PrerenderedHtml.Should().NotBeNull();
        stored.PrerenderedHtml!.Should().Contain("var answer = 42;").And.NotContain("// TODO");
    }

    // ── The WRITE: what actually lands on the node ────────────────────────────────────────────

    private static MeshNode NodeWith(object content) =>
        new("SpotTheGap", "Course/Lesson/Exercise") { NodeType = "Edu/Exercise", Content = content };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheWrite_RebuildsEveryDerivedArtefact()
    {
        var node = NodeWith(new MarkdownContent { Content = Workbench, Authors = ["ada"], Tags = ["fp"] });

        var written = MarkdownCodeCellEditor.WithFenceBody(node, "spotgap", "var answer = 42;", Options);

        var content = written.Content.Should().BeOfType<MarkdownContent>().Subject;
        content.Content.Should().Contain("var answer = 42;").And.NotContain("// TODO");

        // CodeSubmissions is what a Run RE-POSTS. A stale one does not merely look wrong, it EXECUTES
        // the old code and renders a confident result for source nobody is looking at.
        content.CodeSubmissions.Should().NotBeNull();
        content.CodeSubmissions!.Single(s => s.Id == "spotgap").Code.Trim().Should().Be("var answer = 42;");

        // Two HTML projections, and the Overview / prerender paths read the NODE-level one rather
        // than the content's — refreshing only the inner field serves the pre-edit page on reload.
        content.PrerenderedHtml.Should().NotBeNull().And.Subject.As<string>()
            .Should().Contain("var answer = 42;");
        written.PreRenderedHtml.Should().Be(content.PrerenderedHtml);

        // Metadata the shape does not derive is not collateral damage.
        content.Authors.Should().Equal("ada");
        content.Tags.Should().Equal("fp");
    }

    [Fact]
    public void TheWrite_HandlesEveryShapeMarkdownContentArrivesIn()
    {
        // 🚨 The silent-failure shape this repository names first: content is typed on its own hub,
        // a bare string elsewhere, and a JsonElement across a query seam. A typed-only read returns
        // null for the last two, so the save would do NOTHING on pages that render perfectly.
        var asString = MarkdownCodeCellEditor.WithFenceBody(
            NodeWith(Workbench), "spotgap", "var answer = 1;", Options);
        MarkdownOverviewLayoutArea.GetMarkdownContent(asString).Should().Contain("var answer = 1;");
        asString.PreRenderedHtml.Should().NotBeNull("even a bare-string body has a node-level projection");

        var element = JsonSerializer.SerializeToElement(
            new { type = "MarkdownDocument", content = Workbench }, Options);
        var asElement = MarkdownCodeCellEditor.WithFenceBody(
            NodeWith(element), "spotgap", "var answer = 2;", Options);
        MarkdownOverviewLayoutArea.GetMarkdownContent(asElement).Should().Contain("var answer = 2;");
    }

    [Fact]
    public void TheWrite_LeavesTheNodeUNTOUCHEDWhenTheFenceIsGone()
    {
        // Returning the node unchanged makes the merge patch EMPTY. The alternative — "write the
        // whole body" — turns a fence someone renamed into a wiped exercise.
        var node = NodeWith(new MarkdownContent { Content = Workbench });

        MarkdownCodeCellEditor.WithFenceBody(node, "nosuchcell", "x", Options)
            .Should().BeSameAs(node);
    }

    [Fact]
    public void TheWrite_LeavesANonMarkdownNodeAlone()
    {
        // Unreachable from a rendered markdown cell, so it is left alone rather than thrown at —
        // a keystroke is not the place to surface a shape nobody can produce here.
        var node = NodeWith(new { some = "other shape" });

        MarkdownCodeCellEditor.WithFenceBody(node, "spotgap", "x", Options)
            .Should().BeSameAs(node);
    }
}
