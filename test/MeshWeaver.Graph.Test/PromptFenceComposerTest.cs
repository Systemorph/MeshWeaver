using Markdig.Syntax;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The ```` ```prompt ```` fence (#2511): a course page authors a suggested AI prompt as a fenced
/// block, and it must reach the learner as an EDITABLE composer whose Submit starts a real thread —
/// not as static, unrunnable code.
///
/// <para>The fence is lowered onto the EXISTING layout-area marker
/// (<c>&lt;div class='layout-area' data-address=… data-area=… data-area-id=…&gt;</c>), the one
/// vocabulary every client already hydrates, pointing at the node's own <c>Prompt</c> area — so the
/// composer reaches every client with no new marker to teach anyone. See
/// <c>Doc/Architecture/MarkdownFenceExtensions</c>.</para>
///
/// <para>🚨 The degradation rule is asserted here too: the marker WRAPS the read-only fenced block,
/// so a client that does not hydrate layout areas still shows the authored prompt. A fence must
/// never render as LESS than it did before the extension existed — and this repository is the only
/// place that can see it, because every renderer lives in MeshWeaver.Plugins.</para>
/// </summary>
public class PromptFenceComposerTest
{
    private const string LessonPath = "Edu/AdvancedBusinessRules/01-WhyRules";

    private static string Render(string markdown, string? currentNodePath) =>
        Markdig.Markdown.ToHtml(markdown, MarkdownExtensions.CreateMarkdownPipeline(null, currentNodePath));

    [Fact]
    public void PromptFence_LowersToTheNodesComposerLayoutArea()
    {
        var html = Render("```prompt\nShow two versions of the same movement report.\n```", LessonPath);

        html.Should().Contain($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'",
            "the prompt fence reuses the marker every client already hydrates");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Address}='{LessonPath}'",
            "the composer is served by the page's OWN node hub");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'",
            "the area is the node's prompt composer");
    }

    [Fact]
    public void TheAuthoredPromptRidesTheMarkerAsItsAreaId()
    {
        const string prompt = "Show two versions of the same movement report.";
        var html = Render($"```prompt\n{prompt}\n```", LessonPath);

        var encoded = PromptFence.EncodeDraft(prompt);
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.AreaId}='{encoded}'",
            "the composer is pre-filled from the fence body, carried on the area reference");
        PromptFence.DecodeDraft(encoded).Should().Be(prompt,
            "the area id round-trips the authored text exactly");
    }

    [Fact]
    public void AMultiLinePromptSurvivesTheAreaIdRoundTrip()
    {
        // Base64url, not raw text: an area id travels through hrefs and JSON-pointer segments, so a
        // prompt containing '/', '?', '&' or a newline would be mangled — or silently split into
        // reference PARAMETERS (LayoutAreaReference.ParseParameters splits Id on '?' and '&').
        const string prompt = "Compare A/B.\nThen ask: why? And what if x=1&y=2?";
        var encoded = PromptFence.EncodeDraft(prompt);

        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$",
            "the id must survive URL and JSON-pointer encoding untouched");
        PromptFence.DecodeDraft(encoded).Should().Be(prompt);
    }

    [Fact]
    public void AMalformedAreaIdDegradesToNoDraft_NeverAThrowOnARenderPath()
    {
        PromptFence.DecodeDraft("!!!not base64!!!").Should().BeNull();
        PromptFence.DecodeDraft("").Should().BeNull();
        PromptFence.DecodeDraft(null).Should().BeNull();
    }

    [Fact]
    public void TheMarkerWrapsTheReadOnlyFence_SoAnUnhydratedClientStillShowsThePrompt()
    {
        // 🚨 The degradation rule. Clients that hydrate the layout-area marker REPLACE the div and
        // drop its children (MarkdownHtmlRenderer.RenderLayoutArea); clients that don't render the
        // children — the authored prompt, exactly as it read before this extension existed.
        var html = Render("```prompt\nExplain the unexplained balancing line.\n```", LessonPath);

        html.Should().MatchRegex(
            $"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'[^>]*>\\s*<pre>",
            "the read-only fence sits INSIDE the marker, not instead of it and not beside it");
        html.Should().Contain("Explain the unexplained balancing line.",
            "the authored prompt survives for a client that cannot hydrate the marker");
    }

    [Fact]
    public void WithoutANodePath_ThePromptFenceStaysAPlainFencedBlock()
    {
        // No owning node ⇒ no hub to serve the composer area. Emitting a marker with an empty
        // address is the ownerless-address storm shape; render the fence read-only instead.
        var html = Render("```prompt\nWhat changed?\n```", currentNodePath: null);

        html.Should().NotContain($"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'");
        html.Should().Contain("What changed?");
        html.Should().Contain("<pre>");
    }

    [Fact]
    public void AnEmptyPromptFenceStillRendersAComposer()
    {
        var html = Render("```prompt\n```", LessonPath);

        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.AreaId}=''",
            "an empty fence still gives the learner a composer — there is simply nothing to seed it with");
    }

    [Fact]
    public void APromptFenceIsNeverSubmittedToTheKernel()
    {
        // Prose for an agent, never source for Roslyn — even if an author writes a --render on it.
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, LessonPath);
        var document = Markdig.Markdown.Parse("```prompt --render X\nSummarise the movement report.\n```", pipeline);

        foreach (var block in document.Descendants<ExecutableCodeBlock>())
        {
            block.Initialize();
            block.SubmitCode.Should().BeNull("a prompt fence carries no code submission");
        }
    }

    [Fact]
    public void OtherFencesAreUntouched()
    {
        var mermaid = Render("```mermaid\ngraph TD; A-->B;\n```", LessonPath);
        mermaid.Should().Contain("class='mermaid'");
        mermaid.Should().NotContain($"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'");

        var csharp = Render("```csharp\nvar x = 1;\n```", LessonPath);
        csharp.Should().NotContain($"data-{LayoutAreaMarkdownRenderer.Area}='{PromptFence.AreaName}'");
        csharp.Should().Contain("var x = 1;");
    }

    [Fact]
    public void TheComposer_IsCompact_SoSubmitOpensTheThreadFullPage()
    {
        var composer = MeshNodeLayoutAreas.PromptComposer(LessonPath, "Show two versions.");

        composer.InitialDraft.Should().Be("Show two versions.",
            "the composer is pre-filled with the authored prompt, editable in place");
        composer.HideEmptyState.Should().BeTrue(
            "compact mode is what makes ThreadChatView navigate to the new thread FULL PAGE on submit "
            + "instead of opening it in the side panel — the whole point of #2511");
        composer.InitialContext.Should().Be(LessonPath,
            "the lesson the prompt was authored on is the thread's context");
        composer.InitialContextDisplayName.Should().Be("01-WhyRules");
        composer.ThreadPath.Should().BeNull("the thread does not exist until Submit");
    }

    [Fact]
    public void AComposerWithNoDraftCarriesNoDraft()
    {
        MeshNodeLayoutAreas.PromptComposer(LessonPath, null)
            .InitialDraft.Should().BeNull();
    }
}
