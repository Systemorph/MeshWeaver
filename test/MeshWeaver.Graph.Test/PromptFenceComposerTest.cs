using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>Temporary reduced form — proves the pre-change behaviour is RED. Restored after.</summary>
public class PromptFenceComposerTest
{
    private const string LessonPath = "Edu/AdvancedBusinessRules/01-WhyRules";

    private static string Render(string markdown, string? currentNodePath) =>
        Markdig.Markdown.ToHtml(markdown, MarkdownExtensions.CreateMarkdownPipeline(null, currentNodePath));

    [Fact]
    public void PromptFence_LowersToTheNodesComposerLayoutArea()
    {
        var html = Render("```prompt\nShow two versions of the same movement report.\n```", LessonPath);

        html.Should().Contain($"class='{LayoutAreaMarkdownRenderer.LayoutArea}'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Address}='{LessonPath}'");
        html.Should().Contain($"data-{LayoutAreaMarkdownRenderer.Area}='Prompt'");
    }
}
