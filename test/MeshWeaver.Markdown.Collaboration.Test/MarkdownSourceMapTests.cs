using System;
using FluentAssertions;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

/// <summary>
/// Tests that MarkdownSourceMap correctly maps rendered plain text positions
/// back to markdown source positions across all formatting types.
/// The map must work for browser text selections that span across
/// bold, italic, headers, lists, code, math, and existing comment markers.
/// </summary>
public class MarkdownSourceMapTests
{
    /// <summary>
    /// Helper: given markdown and a rendered text selection, verify the map finds it
    /// and the source positions point to the correct range in the markdown.
    /// </summary>
    private static void AssertSelectionMaps(string markdown, string selectedText, string expectedSourceFragment)
    {
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(markdown);

        var idx = plainText.IndexOf(selectedText, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, $"'{selectedText}' should be found in rendered text: '{plainText}'");

        var srcStart = map[idx];
        var srcEnd = (idx + selectedText.Length) < map.Length
            ? map[idx + selectedText.Length]
            : markdown.Length;

        var sourceSlice = markdown[srcStart..srcEnd];
        sourceSlice.Should().Contain(expectedSourceFragment,
            $"Source range [{srcStart}..{srcEnd}] should contain the markdown source for '{selectedText}'");
    }

    [Fact]
    public void PlainText_ExactMatch()
    {
        var md = "Hello world, this is plain text.";
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(md);

        plainText.Should().Contain("Hello world");
        var idx = plainText.IndexOf("Hello world");
        map[idx].Should().Be(0, "Plain text should map 1:1 to source");
    }

    [Fact]
    public void Bold_StripsStars()
    {
        var md = "This is **bold text** in a sentence.";
        AssertSelectionMaps(md, "bold text", "bold text");
    }

    [Fact]
    public void Italic_StripsUnderscores()
    {
        var md = "This is _italic text_ in a sentence.";
        AssertSelectionMaps(md, "italic text", "italic text");
    }

    [Fact]
    public void BoldItalic_Combined()
    {
        var md = "This is ***bold and italic*** together.";
        AssertSelectionMaps(md, "bold and italic", "bold and italic");
    }

    [Fact]
    public void Heading_StripsHashmarks()
    {
        var md = "# My Heading\n\nSome paragraph text.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("My Heading");
        AssertSelectionMaps(md, "My Heading", "My Heading");
    }

    [Fact]
    public void SelectionAcrossHeadingAndParagraph()
    {
        var md = "## Expected Loss\n\nThe expected loss is the probability-weighted average.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("Expected Loss");
        plainText.Should().Contain("probability-weighted");
    }

    [Fact]
    public void NumberedList_StripsNumbersAndDots()
    {
        var md = "1. First item\n2. Second item\n3. Third item";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("First item");
        plainText.Should().Contain("Second item");
        AssertSelectionMaps(md, "Second item", "Second item");
    }

    [Fact]
    public void BulletList_StripsDashes()
    {
        var md = "- Alpha\n- Beta\n- Gamma";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("Alpha");
        AssertSelectionMaps(md, "Beta", "Beta");
    }

    [Fact]
    public void Link_StripsMarkup()
    {
        var md = "Click [this link](https://example.com) to continue.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("this link");
        plainText.Should().NotContain("https://example.com");
    }

    [Fact]
    public void InlineCode_Preserved()
    {
        var md = "Use the `IndexOf` method for searching.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("IndexOf");
    }

    [Fact]
    public void SelectionSpanningBoldAndPlain()
    {
        // User selects "satellite entities alongside" which spans from bold into plain text
        var md = "Annotations are stored as **satellite entities** alongside the document.";
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(md);

        var selection = "satellite entities alongside";
        var idx = plainText.IndexOf(selection);
        idx.Should().BeGreaterThanOrEqualTo(0, $"Should find '{selection}' in: '{plainText}'");

        // The source range should span from inside ** to after **
        var srcStart = map[idx];
        var srcEnd = (idx + selection.Length) < map.Length ? map[idx + selection.Length] : md.Length;
        var source = md[srcStart..srcEnd];
        source.Should().Contain("satellite entities");
        source.Should().Contain("alongside");
    }

    [Fact]
    public void SelectionAcrossMultipleFormattingTypes()
    {
        // Selection spans bold → plain → italic
        var md = "The **quick** brown _fox_ jumps.";
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(md);

        var selection = "quick brown fox";
        var idx = plainText.IndexOf(selection);
        idx.Should().BeGreaterThanOrEqualTo(0);

        var srcStart = map[idx];
        var srcEnd = (idx + selection.Length) < map.Length ? map[idx + selection.Length] : md.Length;
        var source = md[srcStart..srcEnd];
        // Source range spans from inside ** through plain text to inside _
        source.Should().Contain("quick");
        source.Should().Contain("brown");
        source.Should().Contain("fox");
    }

    [Fact]
    public void WithExistingCommentMarkers()
    {
        // Markdown already has comment markers — they should be stripped before mapping
        var rawMd = "Some <!--comment:c1-->existing comment<!--/comment:c1--> text and **bold stuff** here.";

        // Strip annotations first (as the handler does)
        var cleanMd = MarkdownAnnotationParser.StripAllMarkers(rawMd);
        var annotationMap = MarkdownAnnotationParser.BuildCleanToAnnotatedMap(rawMd);
        var (plainText, cleanMap) = MarkdownSourceMap.BuildRenderedToSourceMap(cleanMd);

        // Find "bold stuff" in rendered text
        var idx = plainText.IndexOf("bold stuff");
        idx.Should().BeGreaterThanOrEqualTo(0);

        // Map: rendered → clean → raw
        var cleanStart = cleanMap[idx];
        var cleanEnd = cleanMap[idx + "bold stuff".Length];
        var rawStart = cleanStart < annotationMap.Length ? annotationMap[cleanStart] : rawMd.Length;
        var rawEnd = cleanEnd < annotationMap.Length ? annotationMap[cleanEnd] : rawMd.Length;

        var rawSlice = rawMd[rawStart..rawEnd];
        rawSlice.Should().Contain("bold stuff");
    }

    [Fact]
    public void RealisticReinsurancePricingContent()
    {
        var md = @"## Pricing Components

The reinsurance price is built from several components:

1. **Expected Loss**
The expected loss is the probability-weighted average of all possible loss outcomes.

2. **Risk Load**
Beyond expected loss, reinsurers add a risk load to compensate for uncertainty.

3. **Expense Load**
Operating costs are built into the final price through an expense load.

4. **Profit Margin**
The target return on allocated capital.";

        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(md);

        // The user selects across numbered list items — this is the exact scenario
        // that failed with IndexOf because the rendered text doesn't have ** or ##
        var selection = "Expected Loss\nThe expected loss is the probability-weighted average";
        var idx = plainText.IndexOf(selection);
        idx.Should().BeGreaterThanOrEqualTo(0, $"Selection should be found in rendered text.\nRendered:\n{plainText}");

        // Verify the source positions span correctly
        var srcStart = map[idx];
        var srcEnd = (idx + selection.Length) < map.Length ? map[idx + selection.Length] : md.Length;
        srcStart.Should().BeGreaterThan(0);
        srcEnd.Should().BeGreaterThan(srcStart);
    }

    [Fact]
    public void BlockquoteContent()
    {
        var md = "> This is a blockquote\n> with multiple lines.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("This is a blockquote");
    }

    [Fact]
    public void StrikethroughText()
    {
        var md = "This is ~~deleted~~ text.";
        var (plainText, _) = MarkdownSourceMap.BuildRenderedToSourceMap(md);
        plainText.Should().Contain("deleted");
    }

    [Fact]
    public void EmptyMarkdown()
    {
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap("");
        plainText.Should().BeEmpty();
        map.Should().BeEmpty();
    }

    [Fact]
    public void NullMarkdown()
    {
        var (plainText, map) = MarkdownSourceMap.BuildRenderedToSourceMap(null!);
        plainText.Should().BeEmpty();
        map.Should().BeEmpty();
    }
}
