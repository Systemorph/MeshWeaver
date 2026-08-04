using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// Pins the HTML shape of executable code blocks rendered as notebook cells: an executable block that
/// shows its code (<c>--render X --show-code</c>) is wrapped in a cell frame holding the code, the
/// kernel result area directly below it, and — on the frame's BOTTOM edge — the toolbar marker the
/// clients turn into the Run button. Blocks that hide their code, and plain documentation-only
/// fences, keep their previous shape.
/// <para>The ORDER is the contract, not a detail: every client (Blazor <c>MarkdownHtmlRenderer</c>,
/// the React <c>"toolbar"</c> segment, React Native's <c>RunCell</c>) renders these segments in
/// document order, so this single emission decides where the Run button sits on all three.</para>
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

        // Order: code, then output, then the toolbar LAST — Run sits at the foot of the cell,
        // below the code window (composer-style), never above it.
        var toolbarIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellToolbarClass, StringComparison.Ordinal);
        var codeIdx = html.IndexOf("code-content", StringComparison.Ordinal);
        var outputIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellOutputClass, StringComparison.Ordinal);
        codeIdx.Should().BeLessThan(outputIdx, "the run's output belongs directly under the code");
        outputIdx.Should().BeLessThan(toolbarIdx, "the Run toolbar is the composer bar at the cell's foot");
    }

    [Fact]
    public void ShowHeaderVariant_AlsoPutsTheToolbarLast()
    {
        // --show-header renders the fence header instead of a bare code block, through a different
        // branch of the renderer. It must reach the same cell shape — the earlier layout put the
        // toolbar first for BOTH branches, so fixing only one would leave a silent inconsistency.
        var html = Render("```csharp --render HeaderDemo --show-header\n1 + 1\n```");

        var toolbarIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellToolbarClass, StringComparison.Ordinal);
        var codeIdx = html.IndexOf("code-content", StringComparison.Ordinal);
        var outputIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellOutputClass, StringComparison.Ordinal);

        toolbarIdx.Should().BeGreaterThan(-1);
        codeIdx.Should().BeLessThan(outputIdx);
        outputIdx.Should().BeLessThan(toolbarIdx);
    }

    [Fact]
    public void CellFrame_ClosesAfterTheToolbar()
    {
        // The toolbar must be INSIDE the cell frame, not orphaned after it — otherwise it renders as
        // a loose bar under the card with none of the frame's border/background, which reads as a
        // stray button rather than the cell's own composer bar.
        var html = Render("```csharp --render FrameDemo --show-code\n1 + 1\n```");

        var cellIdx = html.IndexOf($"<div class=\"{ExecutableCodeBlockRenderer.CellClass}\">", StringComparison.Ordinal);
        var toolbarIdx = html.IndexOf(ExecutableCodeBlockRenderer.CellToolbarClass, StringComparison.Ordinal);
        var closeIdx = html.IndexOf("</div>", toolbarIdx, StringComparison.Ordinal);

        cellIdx.Should().BeLessThan(toolbarIdx);
        // …toolbar div closes, then the frame closes: two consecutive closing tags after the marker.
        html[closeIdx..].Should().StartWith("</div></div>",
            "the toolbar closes and the cell frame closes right after it");
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
