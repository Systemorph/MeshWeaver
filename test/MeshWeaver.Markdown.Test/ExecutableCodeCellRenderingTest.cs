using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Pins the HTML shape of executable code blocks rendered as notebook cells: an executable block that
/// shows its code (<c>--render X --show-code</c>) is wrapped in a cell frame carrying a toolbar marker
/// (which the Blazor renderer turns into the Run button) with the code and the kernel result area
/// inside the same frame — code first, output directly below. Blocks that hide their code, and plain
/// documentation-only fences, keep their previous shape.
/// </summary>
public class ExecutableCodeCellRenderingTest
{
    private static string Render(string markdown)
    {
        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(null, null);
        return Markdig.Markdown.ToHtml(markdown, pipeline);
    }

    [Fact]
    public void RenderWithShowCode_WrapsCodeAndResultAreaInCellWithToolbar()
    {
        var html = Render("```csharp --render MyDemo --show-code\nControls.Markdown(\"hi\")\n```");

        html.Should().Contain($"<div class=\"{ExecutableCodeBlockRenderer.CellClass}\">");
        html.Should().Contain(
            $"<div class=\"{ExecutableCodeBlockRenderer.CellToolbarClass}\" " +
            $"{ExecutableCodeBlockRenderer.SubmissionIdAttribute}=\"mydemo\" " +
            $"{ExecutableCodeBlockRenderer.LanguageAttribute}=\"csharp\"></div>");
        html.Should().Contain("code-content", "the cell must display the source");
        html.Should().Contain($"<div class=\"{ExecutableCodeBlockRenderer.CellOutputClass}\">");
        html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "the kernel result area must sit inside the output segment");

        // Order: toolbar before code, code before output — the notebook reading shape.
        var toolbarIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellToolbarClass, StringComparison.Ordinal);
        var codeIdx = html.IndexOf("code-content", StringComparison.Ordinal);
        var outputIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellOutputClass, StringComparison.Ordinal);
        toolbarIdx.Should().BeLessThan(codeIdx);
        codeIdx.Should().BeLessThan(outputIdx);
    }

    [Fact]
    public void PythonFence_CarriesPythonLanguageOnToolbar()
    {
        var html = Render("```python --render PyDemo --show-code\nprint(1)\n```");

        html.Should().Contain($"{ExecutableCodeBlockRenderer.LanguageAttribute}=\"python\"");
    }

    [Fact]
    public void RenderWithoutShowCode_KeepsBareResultArea_NoCellFrame()
    {
        var html = Render("```csharp --render HiddenDemo\nControls.Markdown(\"hi\")\n```");

        html.Should().NotContain(ExecutableCodeBlockRenderer.CellClass);
        html.Should().NotContain(ExecutableCodeBlockRenderer.CellToolbarClass);
        html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "the live result area still renders");
    }

    [Fact]
    public void PlainFence_StaysAPlainCodeBlock()
    {
        var html = Render("```csharp\nvar x = 1;\n```");

        html.Should().NotContain(ExecutableCodeBlockRenderer.CellClass);
        html.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "documentation-only fences never execute");
    }

    [Fact]
    public void KernelPlaceholderReplacement_StillMatchesInsideCellFrame()
    {
        var html = Render("```csharp --render MyDemo --show-code\n1 + 1\n```");

        // The pending/disabled substitutions target the result-area div by its placeholder address;
        // wrapping it in the cell's output segment must not break that match.
        var pending = MarkdownViewLogic.PendingKernelPlaceholder(html);
        pending.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        pending.Should().Contain("markdown-kernel-pending");
        pending.Should().Contain(ExecutableCodeBlockRenderer.CellOutputClass,
            "the notice renders inside the cell's output segment");
    }
}
