using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The composer a markdown ```` ```prompt ```` fence lowers to (#2511) — the <c>Prompt</c> layout
/// area's shape. A course page's suggested AI prompt must reach the learner as an EDITABLE composer
/// whose Submit starts a real thread FULL PAGE, not as static, unrunnable code.
///
/// <para>The fence-to-marker half of the story is asserted where the renderer's own suite lives:
/// <c>MeshWeaver.Markdown.Test/PromptFenceComposerTest</c> in MeshWeaver.Plugins. What is testable
/// here is the platform code that serves the marker.</para>
///
/// <para>🚨 Neither suite can see what a learner sees — every renderer lives in MeshWeaver.Plugins,
/// so the acceptance check is a RENDERED PAGE. See <c>Doc/Architecture/MarkdownFenceExtensions</c>.</para>
/// </summary>
public class PromptComposerAreaTest
{
    private const string LessonPath = "Edu/AdvancedBusinessRules/01-WhyRules";

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
