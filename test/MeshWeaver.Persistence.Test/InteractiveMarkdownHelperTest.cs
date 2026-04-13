using System.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Kernel;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Xunit;
using MdExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for interactive markdown parsing: executable code block extraction,
/// kernel address placeholder replacement, and SubmitCodeRequest generation.
/// </summary>
public class InteractiveMarkdownHelperTest
{
    [Fact]
    public void ExecutableCodeBlocks_AreExtractedFromMarkdown()
    {
        var markdown = """
            # Hello

            ```csharp --execute
            var x = 1 + 2;
            Controls.Markdown($"Result: {x}")
            ```

            Some text after.
            """;

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        var blocks = document.Descendants<ExecutableCodeBlock>().ToList();

        blocks.Should().HaveCount(1);
        blocks[0].Initialize();
        blocks[0].SubmitCode.Should().NotBeNull();
        blocks[0].SubmitCode!.Code.Should().Contain("var x = 1 + 2");
    }

    [Fact]
    public void MultipleExecutableBlocks_AreAllExtracted()
    {
        var markdown = """
            ```csharp --execute
            var a = 1;
            ```

            ```csharp --render
            Controls.Markdown("hello")
            ```

            ```csharp
            // Bare csharp — documentation-only, not submitted.
            var b = 2;
            ```
            """;

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        var blocks = document.Descendants<ExecutableCodeBlock>().ToList();
        foreach (var b in blocks) b.Initialize();

        var submissions = blocks
            .Select(b => b.SubmitCode)
            .Where(s => s != null)
            .ToList();

        // Only --execute and --render submit; bare ```csharp is documentation-only.
        submissions.Should().HaveCount(2);
    }

    [Fact]
    public void KernelAddressPlaceholder_IsEmbeddedInRenderedHtml()
    {
        var markdown = """
            ```csharp --execute
            Controls.Markdown("test")
            ```
            """;

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        var html = document.ToHtml(pipeline);

        html.Should().Contain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            "Pre-rendered HTML should contain __KERNEL_ADDRESS__ placeholder");
    }

    [Fact]
    public void KernelAddressPlaceholder_IsReplacedWithActualAddress()
    {
        var html = $"<div data-address='{ExecutableCodeBlockRenderer.KernelAddressPlaceholder}' data-area-id='abc123'></div>";
        var kernelAddress = AddressExtensions.CreateKernelAddress("test-kernel-id");

        var result = html.Replace(
            ExecutableCodeBlockRenderer.KernelAddressPlaceholder,
            kernelAddress.ToString());

        result.Should().NotContain(ExecutableCodeBlockRenderer.KernelAddressPlaceholder);
        result.Should().Contain("test-kernel-id");
    }

    [Fact]
    public void SubmitCodeRequest_HasUniqueId()
    {
        var markdown = """
            ```csharp --execute
            var x = 1;
            ```

            ```csharp --execute
            var y = 2;
            ```
            """;

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        var blocks = document.Descendants<ExecutableCodeBlock>().ToList();
        foreach (var b in blocks) b.Initialize();

        var submissions = blocks
            .Select(b => b.SubmitCode)
            .Where(s => s != null)
            .ToList();

        submissions.Should().HaveCount(2);
        submissions[0]!.Id.Should().NotBe(submissions[1]!.Id,
            "Each code block should get a unique submission ID");
    }

    /// <summary>
    /// Default behavior: bare fenced blocks (csharp or otherwise) are
    /// documentation-only. Execution requires an explicit --execute or --render flag.
    /// </summary>
    [Fact]
    public void BareBlocks_AreDocumentationOnly()
    {
        var markdown = """
            ```csharp
            var x = 1;
            ```

            ```python
            print("hello")
            ```

            Normal text.
            """;

        var pipeline = MdExtensions.CreateMarkdownPipeline(null, null);
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        var blocks = document.Descendants<ExecutableCodeBlock>().ToList();
        foreach (var b in blocks) b.Initialize();

        var submissions = blocks
            .Select(b => b.SubmitCode)
            .Where(s => s != null)
            .ToList();

        submissions.Should().BeEmpty(
            "bare fenced blocks are documentation-only; execution requires --execute or --render");
    }
}
