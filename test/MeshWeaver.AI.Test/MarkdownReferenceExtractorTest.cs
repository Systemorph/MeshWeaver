#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using FluentAssertions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for MarkdownReferenceExtractor: extraction of @path references,
/// deduplication, removal, and filtering of @agent/ commands.
/// </summary>
public class MarkdownReferenceExtractorTest
{
    #region ExtractReferences — Direct @path syntax

    [Fact]
    public void ExtractReferences_DirectPath_ExtractsPath()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("Check @MeshWeaver/Documentation/AI for details");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("MeshWeaver/Documentation/AI");
        refs[0].OriginalText.Should().Be("@MeshWeaver/Documentation/AI");
    }

    [Fact]
    public void ExtractReferences_MultipleDirect_ExtractsAll()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences(
            "See @ACME/ProductLaunch and @Northwind/Sales for examples");

        refs.Should().HaveCount(2);
        refs[0].Path.Should().Be("ACME/ProductLaunch");
        refs[1].Path.Should().Be("Northwind/Sales");
    }

    [Fact]
    public void ExtractReferences_DirectPathWithDots_ExtractsFull()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @path/to/file.md here");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("path/to/file.md");
    }

    [Fact]
    public void ExtractReferences_DirectPathWithDashes_ExtractsFull()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @my-org/my-project for info");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("my-org/my-project");
    }

    [Fact]
    public void ExtractReferences_DirectPathWithUnderscores_ExtractsFull()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @my_org/my_project for info");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("my_org/my_project");
    }

    #endregion

    #region ExtractReferences — Parentheses @(path) syntax

    [Fact]
    public void ExtractReferences_ParenthesesPath_ExtractsPath()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @(path with spaces) for details");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("path with spaces");
        refs[0].OriginalText.Should().Be("@(path with spaces)");
    }

    [Fact]
    public void ExtractReferences_ParenthesesQuotedPath_ExtractsPath()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @(\"quoted path with spaces\") for details");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("quoted path with spaces");
        refs[0].OriginalText.Should().Be("@(\"quoted path with spaces\")");
    }

    #endregion

    #region ExtractReferences — Quoted @"path" syntax

    [Fact]
    public void ExtractReferences_QuotedPath_ExtractsPath()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @\"path with spaces\" for details");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("path with spaces");
        refs[0].OriginalText.Should().Be("@\"path with spaces\"");
    }

    #endregion

    #region ExtractReferences — Filtering known commands

    [Fact]
    public void ExtractReferences_AgentCommand_IsFiltered()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("Use @agent/TodoAgent for tasks");

        refs.Should().BeEmpty("@agent/ prefix is a known command, not a reference");
    }

    [Fact]
    public void ExtractReferences_ModelCommand_IsFiltered()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences("Switch to @model/gpt-4 for this");

        refs.Should().BeEmpty("@model/ prefix is a known command, not a reference");
    }

    [Fact]
    public void ExtractReferences_MixedCommandAndPath_ExtractsOnlyPath()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences(
            "@agent/TodoAgent check @ACME/ProductLaunch please");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("ACME/ProductLaunch");
    }

    #endregion

    #region ExtractReferences — Overlap handling and priority

    [Fact]
    public void ExtractReferences_ParenthesesTakesPriorityOverDirect()
    {
        // @(MeshWeaver/Docs) should be extracted as parentheses syntax,
        // not as a direct @(MeshWeaver/Docs) match
        var refs = MarkdownReferenceExtractor.ExtractReferences("See @(MeshWeaver/Docs) here");

        refs.Should().ContainSingle();
        refs[0].Path.Should().Be("MeshWeaver/Docs");
        refs[0].OriginalText.Should().Be("@(MeshWeaver/Docs)");
    }

    [Fact]
    public void ExtractReferences_SortedByStartIndex()
    {
        var refs = MarkdownReferenceExtractor.ExtractReferences(
            "First @Alpha then @Beta finally @Gamma");

        refs.Should().HaveCount(3);
        refs[0].Path.Should().Be("Alpha");
        refs[1].Path.Should().Be("Beta");
        refs[2].Path.Should().Be("Gamma");
        refs[0].StartIndex.Should().BeLessThan(refs[1].StartIndex);
        refs[1].StartIndex.Should().BeLessThan(refs[2].StartIndex);
    }

    #endregion

    #region ExtractReferences — Edge cases

    [Fact]
    public void ExtractReferences_Null_ReturnsEmpty()
    {
        MarkdownReferenceExtractor.ExtractReferences(null).Should().BeEmpty();
    }

    [Fact]
    public void ExtractReferences_Empty_ReturnsEmpty()
    {
        MarkdownReferenceExtractor.ExtractReferences("").Should().BeEmpty();
    }

    [Fact]
    public void ExtractReferences_NoReferences_ReturnsEmpty()
    {
        MarkdownReferenceExtractor.ExtractReferences("Just plain text without any references").Should().BeEmpty();
    }

    [Fact]
    public void ExtractReferences_AtSignAlone_ReturnsEmpty()
    {
        // Bare @ should not match (the regex requires at least one path character after @)
        MarkdownReferenceExtractor.ExtractReferences("Email me @ home").Should().BeEmpty();
    }

    #endregion

    #region GetUniquePaths

    [Fact]
    public void GetUniquePaths_DuplicateReferences_ReturnsDistinct()
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(
            "See @ACME/ProductLaunch and also @ACME/ProductLaunch for info");

        paths.Should().ContainSingle();
        paths[0].Should().Be("ACME/ProductLaunch");
    }

    [Fact]
    public void GetUniquePaths_CaseInsensitiveDuplication()
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(
            "See @acme/productlaunch and @ACME/ProductLaunch");

        paths.Should().ContainSingle("case-insensitive comparison should deduplicate");
    }

    [Fact]
    public void GetUniquePaths_MultipleDistinct_ReturnsAll()
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(
            "See @ACME/ProductLaunch and @Northwind/Sales");

        paths.Should().HaveCount(2);
        paths.Should().Contain("ACME/ProductLaunch");
        paths.Should().Contain("Northwind/Sales");
    }

    [Fact]
    public void GetUniquePaths_NullInput_ReturnsEmpty()
    {
        MarkdownReferenceExtractor.GetUniquePaths(null).Should().BeEmpty();
    }

    [Fact]
    public void GetUniquePaths_FiltersCommands()
    {
        var paths = MarkdownReferenceExtractor.GetUniquePaths(
            "@agent/TodoAgent check @ACME/ProductLaunch");

        paths.Should().ContainSingle();
        paths[0].Should().Be("ACME/ProductLaunch");
    }

    #endregion

    #region RemoveReferenceByPath

    [Fact]
    public void RemoveReferenceByPath_DirectPath_RemovesAndCleansWhitespace()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "Check @ACME/ProductLaunch for details", "ACME/ProductLaunch");

        result.Should().Be("Check for details");
    }

    [Fact]
    public void RemoveReferenceByPath_ParenthesesPath_Removes()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "Check @(path with spaces) for details", "path with spaces");

        result.Should().Be("Check for details");
    }

    [Fact]
    public void RemoveReferenceByPath_QuotedPath_Removes()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "Check @\"path with spaces\" for details", "path with spaces");

        result.Should().Be("Check for details");
    }

    [Fact]
    public void RemoveReferenceByPath_CaseInsensitive_Removes()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "Check @ACME/ProductLaunch for details", "acme/productlaunch");

        result.Should().Be("Check for details");
    }

    [Fact]
    public void RemoveReferenceByPath_NonExistentPath_ReturnsOriginal()
    {
        const string original = "Check @ACME/ProductLaunch for details";
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(original, "Nonexistent/Path");

        result.Should().Be(original);
    }

    [Fact]
    public void RemoveReferenceByPath_RemovesOnlyFirst_WhenDuplicate()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "@ACME/ProductLaunch and @ACME/ProductLaunch", "ACME/ProductLaunch");

        // After removing first occurrence and trimming, second one remains
        result.Should().Contain("@ACME/ProductLaunch");
    }

    [Fact]
    public void RemoveReferenceByPath_AtStartOfString_Removes()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "@ACME/ProductLaunch is great", "ACME/ProductLaunch");

        result.Should().Be("is great");
    }

    [Fact]
    public void RemoveReferenceByPath_AtEndOfString_Removes()
    {
        var result = MarkdownReferenceExtractor.RemoveReferenceByPath(
            "See @ACME/ProductLaunch", "ACME/ProductLaunch");

        result.Should().Be("See");
    }

    #endregion

    #region RemoveReference

    [Fact]
    public void RemoveReference_ValidReference_Removes()
    {
        const string markdown = "Hello @World/Test goodbye";
        var refs = MarkdownReferenceExtractor.ExtractReferences(markdown);
        refs.Should().ContainSingle();

        var result = MarkdownReferenceExtractor.RemoveReference(markdown, refs[0]);

        result.Should().Be("Hello goodbye");
    }

    [Fact]
    public void RemoveReference_NullMarkdown_ReturnsNull()
    {
        var reference = new ExtractedReference("path", 0, 5, "@path");
        var result = MarkdownReferenceExtractor.RemoveReference(null!, reference);

        result.Should().BeNull();
    }

    [Fact]
    public void RemoveReference_EmptyMarkdown_ReturnsEmpty()
    {
        var reference = new ExtractedReference("path", 0, 5, "@path");
        var result = MarkdownReferenceExtractor.RemoveReference("", reference);

        result.Should().BeEmpty();
    }

    [Fact]
    public void RemoveReference_InvalidPosition_ReturnsOriginal()
    {
        const string markdown = "Hello @World/Test goodbye";
        var invalidRef = new ExtractedReference("World/Test", -1, 5, "@World/Test");

        var result = MarkdownReferenceExtractor.RemoveReference(markdown, invalidRef);

        result.Should().Be(markdown);
    }

    [Fact]
    public void RemoveReference_TextMovedButStillPresent_FindsBySearch()
    {
        // Simulate text that has been modified so the original position doesn't match,
        // but the original text is still present elsewhere
        const string markdown = "Prefix was added Hello @World/Test goodbye";
        // Create a reference with the old position (before "Prefix was added " was inserted)
        var oldRef = new ExtractedReference("World/Test", 6, 17, "@World/Test");

        var result = MarkdownReferenceExtractor.RemoveReference(markdown, oldRef);

        result.Should().Be("Prefix was added Hello goodbye");
    }

    #endregion

    #region Position tracking

    [Fact]
    public void ExtractReferences_PositionTracking_MatchesOriginalText()
    {
        const string markdown = "Start @First/Path middle @Second/Path end";
        var refs = MarkdownReferenceExtractor.ExtractReferences(markdown);

        foreach (var r in refs)
        {
            var actual = markdown.Substring(r.StartIndex, r.EndIndex - r.StartIndex);
            actual.Should().Be(r.OriginalText, $"position {r.StartIndex}-{r.EndIndex} should match original text");
        }
    }

    [Fact]
    public void ExtractReferences_EndIndex_IsExclusive()
    {
        const string markdown = "@Test/Path rest of text";
        var refs = MarkdownReferenceExtractor.ExtractReferences(markdown);

        refs.Should().ContainSingle();
        refs[0].StartIndex.Should().Be(0);
        refs[0].EndIndex.Should().Be("@Test/Path".Length);
    }

    #endregion
}
