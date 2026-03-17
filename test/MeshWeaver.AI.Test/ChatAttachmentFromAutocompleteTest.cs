using FluentAssertions;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests the reference extraction and removal logic used when
/// a chat autocomplete item is selected and converted to an attachment chip.
/// The flow: autocomplete inserts "@SomePath" -> extract path -> create chip -> remove @text.
/// </summary>
public class ChatAttachmentFromAutocompleteTest
{
    /// <summary>Tests that removing a reference by path leaves a clean message.</summary>
    [Theory]
    [InlineData("ask about @ACME/Reports", "ACME/Reports", "ask about")]
    [InlineData("@ACME/Reports details", "ACME/Reports", "details")]
    [InlineData("@ACME/Reports", "ACME/Reports", "")]
    [InlineData("check @ACME/Reports and @Other/Node", "ACME/Reports", "check and @Other/Node")]
    public void AutocompleteReference_RemovedFromText_LeavesCleanMessage(
        string input, string path, string expectedOutput)
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(input, path);
        result.Should().Be(expectedOutput);
    }

    /// <summary>Tests content: prefix extraction.</summary>
    [Fact]
    public void ContentReference_ExtractedCorrectly()
    {
        // When autocomplete inserts a content path, it uses the content: prefix
        var markdown = "see @content:docs/readme.md for details";
        var refs = MarkdownReferenceExtractor.GetUniquePaths(markdown);

        // The direct reference pattern matches @content:docs/readme.md as "content:docs/readme.md"
        refs.Should().Contain(r => r.Contains("content"));
    }

    /// <summary>Tests that lowercase agent/ paths are filtered as commands.</summary>
    [Fact]
    public void AgentPaths_FilteredAsCommands()
    {
        // Agent paths starting with lowercase "agent/" should be filtered (they are commands)
        var paths = MarkdownReferenceExtractor.GetUniquePaths("use @agent/Research for this");
        paths.Should().BeEmpty("lowercase @agent/ is a command, not a reference");
    }

    /// <summary>Tests that uppercase Agent/ paths are kept as references.</summary>
    [Fact]
    public void AgentNamespacePaths_NotFiltered()
    {
        // Agent paths starting with uppercase "Agent/" are real namespace paths
        var paths = MarkdownReferenceExtractor.GetUniquePaths("reference @Agent/Research");
        paths.Should().ContainSingle()
            .Which.Should().Be("Agent/Research");
    }

    /// <summary>Tests case-insensitive deduplication of references.</summary>
    [Fact]
    public void DuplicateReferences_DeduplicatedCaseInsensitive()
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths("@ACME/Reports and @acme/reports");
        paths.Should().HaveCount(1, "same path with different case should be deduplicated");
    }

    /// <summary>Tests extraction of multiple references.</summary>
    [Fact]
    public void MultipleReferences_AllExtracted()
    {
        var markdown = "compare @ACME/Reports with @Systemorph/Docs";
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);

        paths.Should().HaveCount(2);
        paths.Should().Contain("ACME/Reports");
        paths.Should().Contain("Systemorph/Docs");
    }

    /// <summary>Tests removal of content-prefixed references.</summary>
    [Fact]
    public void RemoveReference_WithContentPrefix_Works()
    {
        var input = "see @content:docs/readme.md here";
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(input, "content:docs/readme.md");
        result.Should().Be("see here");
    }

    /// <summary>Tests graceful handling of empty/null input.</summary>
    [Fact]
    public void EmptyOrNullText_HandledGracefully()
    {
        MarkdownReferenceExtractor.GetUniquePaths(null).Should().BeEmpty();
        MarkdownReferenceExtractor.GetUniquePaths("").Should().BeEmpty();
        MarkdownReferenceExtractor.GetUniquePaths("   ").Should().BeEmpty();

        MarkdownReferenceExtractor.RemoveReferenceByPath("", "path").Should().Be("");
    }

    /// <summary>Tests extraction of paths with hyphens and dots.</summary>
    [Fact]
    public void PathWithHyphensAndDots_ExtractedCorrectly()
    {
        var markdown = "file @content:my-docs/report-2024.v2.md";
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);
        paths.Should().ContainSingle()
            .Which.Should().Be("content:my-docs/report-2024.v2.md");
    }

    /// <summary>Tests extraction of unified path with address prefix.</summary>
    [Fact]
    public void UnifiedPathWithAddress_ExtractedCorrectly()
    {
        // Unified path format: {address}/{collectionName}:{filePath}
        var markdown = "see @ACME/content:readme.md for details";
        var paths = MarkdownReferenceExtractor.GetUniquePaths(markdown);
        paths.Should().ContainSingle()
            .Which.Should().Be("ACME/content:readme.md");
    }

    /// <summary>Tests removal of unified path with address prefix.</summary>
    [Fact]
    public void UnifiedPathWithAddress_RemovedCorrectly()
    {
        var input = "check @Systemorph/content:docs/guide.md here";
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(input, "Systemorph/content:docs/guide.md");
        result.Should().Be("check here");
    }
}
