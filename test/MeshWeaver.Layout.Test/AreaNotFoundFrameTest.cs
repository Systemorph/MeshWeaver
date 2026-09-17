using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>The frame a missing renderer serves lands inside somebody's DOCUMENT, not only on a
/// developer's page.</b> A node page embeds other packages' areas by reference —
/// <c>MarkdownOverviewLayoutArea</c>'s approvals and signatures sections are two — and each guard
/// asks the mesh INDEX whether the package's desk node exists, which is not a question about the
/// replica that ends up answering. When those disagree, this frame is what the reader gets.
///
/// <para>Measured 2026-09-17 on memex.systemorph.com: the maintainer opened
/// <c>CollaborationNotus/PrereadToNotus20260918</c> — a customer letter — and the page carried
/// <i>"No renderer is registered for area Approvals on hub Approvals/Workspace"</i> followed by
/// sixty framework area names. The portal was mid-roll: the <c>Approvals/Desk</c> NodeType's
/// compiled assembly is stamped with a framework identity (<c>saec4a2d…</c>) that neither READY
/// replica runs (<c>s2902ab1…</c>), so its areas were never registered there while the shared
/// <c>compilationStatus</c> still read <c>Ok</c>.</para>
///
/// <para>So this frame has TWO audiences and they need opposite things: a reader needs one sentence
/// in their own language, an operator needs the area, the hub and what IS registered. It serves
/// both — the sentence first, the diagnostic folded away beneath it — and that is what these tests
/// pin, in both directions.</para>
/// </summary>
public class AreaNotFoundFrameTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string MissingArea = "Approvals";
    private const string RegisteredArea = "Overview";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(RegisteredArea, Controls.Html("the node's own overview"))
                .WithView("Details", Controls.Html("details")));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private async Task<MarkdownControl> RenderMissingArea()
    {
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(MissingArea));

        var control = await stream.GetControlStream(MissingArea)
            .Should().Within(10.Seconds()).Match(x => x != null);

        return control.Should().BeOfType<MarkdownControl>().Which;
    }

    /// <summary>
    /// What the person reading the page gets: a sentence, in their language, that does not pretend
    /// to know WHY. The frame is classified as a verdict, but its causes genuinely differ — a
    /// module not loaded on this replica, an area renamed, an embed naming an area that never
    /// existed — so "temporary" and "does not exist" would both be guesses. That the rest of the
    /// page is intact is not a guess, and it is the thing a reader of a half-broken document most
    /// needs to be told.
    /// </summary>
    [HubFact]
    public async Task TheReaderGetsALocalizedSentence_BeforeAnyDiagnostic()
    {
        var markdown = (await RenderMissingArea()).Markdown?.ToString() ?? string.Empty;

        markdown.Should().StartWith("This section could not be displayed.",
            "the first thing on the page is for whoever is reading it — the framework diagnostic "
            + "opened a customer letter before this");
        markdown.Should().Contain("The rest of this page is unaffected.");

        markdown.Should().NotContain("layout.areaNotFound.",
            "an unresolved key renders as its own name, which is worse than the English it replaced");

        markdown.IndexOf("This section could not be displayed.", System.StringComparison.Ordinal)
            .Should().BeLessThan(markdown.IndexOf("**Area not found**", System.StringComparison.Ordinal),
                "the sentence comes FIRST; a diagnostic the reader has to scroll past is the defect");
    }

    /// <summary>
    /// The operator half, unchanged and NOT localized: which area, which hub, and what that hub
    /// does register — the three facts that turn this frame from a complaint into a diagnosis.
    /// They move behind a disclosure; they do not go away.
    /// </summary>
    [HubFact]
    public async Task TheDiagnosticSurvives_FoldedAwayUnderTheSentence()
    {
        var markdown = (await RenderMissingArea()).Markdown?.ToString() ?? string.Empty;

        markdown.Should().Contain("<details>", "the diagnostic is one click away, not gone");
        markdown.Should().Contain("Technical details");
        markdown.Should().Contain($"No renderer is registered for area `{MissingArea}`");
        markdown.Should().Contain("on hub `", "the hub that answered is half the diagnosis");
        markdown.Should().Contain($"`{RegisteredArea}`",
            "what IS registered is what tells an operator whether the module failed to load or the "
            + "area was renamed — the two readings this frame cannot distinguish on its own");
    }

    /// <summary>
    /// 🚨 The marker is load-bearing and easy to localize away by accident.
    /// <see cref="AreaFrameClassifier"/> recognises this frame by its <c>Id</c> first and falls
    /// back to the literal <c>**Area not found**</c> in the prose, for a control that lost its id
    /// on the way over (an older peer, a rebuild from partial JSON). Putting the reader's sentence
    /// in FRONT of the diagnostic rather than in place of it is what keeps both working — and this
    /// asserts the fallback specifically, on a control with its id stripped, because the id path
    /// would pass either way and hide a broken marker.
    /// </summary>
    [HubFact]
    public async Task TheClassifierStillRecognisesIt_ByIdAndByMarker()
    {
        var control = await RenderMissingArea();

        AreaFrameClassifier.IsAreaNotFound(control).Should().BeTrue("by id");
        AreaFrameClassifier.IsTransientFrame(control).Should().BeFalse(
            "a missing area is a verdict — nothing is going to replace it");

        var idStripped = control with { Id = null };
        AreaFrameClassifier.IsAreaNotFound(idStripped).Should().BeTrue(
            "the prose fallback is what covers a frame that lost its id in transit, and it matches "
            + "the English marker — so the diagnostic half must never be translated");
    }
}
