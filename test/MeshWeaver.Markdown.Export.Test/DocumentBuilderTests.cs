using System.Linq;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Model;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

public class DocumentBuilderTests
{
    [Fact]
    public void Builds_headings_paragraphs_and_code_blocks()
    {
        var md = """
                 # Introduction

                 Some **bold** and *italic* text.

                 ## Details

                 ```csharp
                 var x = 1;
                 ```
                 """;
        var doc = new DocumentBuilder().Build(
            "Test",
            md,
            new DocumentExportOptions { PageBreakBeforeH1 = false },
            BrandingOptions.Default);

        doc.Elements.OfType<HeadingElement>().Should().HaveCount(2);
        doc.Elements.OfType<ParagraphElement>().Should().NotBeEmpty();
        doc.Elements.OfType<CodeBlockElement>().Should().ContainSingle()
            .Which.Language.Should().Be("csharp");
        doc.TocHeadings.Should().HaveCount(2);
    }

    [Fact]
    public void Emits_page_break_before_h1_when_enabled()
    {
        var md = """
                 # First

                 text

                 # Second

                 text
                 """;
        var doc = new DocumentBuilder().Build(
            "T", md,
            new DocumentExportOptions { PageBreakBeforeH1 = true },
            BrandingOptions.Default);

        // Expect a PageBreak before the second H1 but not the first.
        doc.Elements.OfType<PageBreakElement>().Should().HaveCount(1);
        var indexOfBreak = doc.Elements.ToList().FindIndex(e => e is PageBreakElement);
        doc.Elements[indexOfBreak + 1].Should().BeOfType<HeadingElement>()
            .Which.Level.Should().Be(1);
    }

    [Fact]
    public void Honours_explicit_pagebreak_marker()
    {
        var md = """
                 Before

                 \newpage

                 After
                 """;
        var doc = new DocumentBuilder().Build(
            "T", md, new DocumentExportOptions(), BrandingOptions.Default);

        doc.Elements.OfType<PageBreakElement>().Should().HaveCount(1);
    }

    [Fact]
    public void Chapter_break_inserted_between_children()
    {
        var chapters = new[]
        {
            ("Chapter 1", "# A\n\ncontent"),
            ("Chapter 2", "# B\n\ncontent"),
        };
        var doc = new DocumentBuilder().Build(
            "Book",
            chapters,
            new DocumentExportOptions
            {
                IncludeChildren = true,
                PageBreakBetweenChildren = true,
                PageBreakBeforeH1 = false
            },
            BrandingOptions.Default);

        doc.Elements.OfType<ChapterBreakElement>().Should().ContainSingle()
            .Which.Title.Should().Be("Chapter 2");
    }

    /// <summary>
    /// #1373 — <c>~~strike~~</c> is struck through and NOTHING else.
    ///
    /// <para>Markdig folds every emphasis flavour into one <c>EmphasisInline</c> and distinguishes
    /// them by the delimiter CHARACTER; the builder tested only the COUNT, so the doubled <c>~~</c>
    /// satisfied "strong" as well and the run came out bold AND struck. It reached paper in both
    /// formats — <c>&lt;strong&gt;&lt;s&gt;…&lt;/s&gt;&lt;/strong&gt;</c> in the PDF print,
    /// <c>&lt;w:b/&gt;</c> beside <c>&lt;w:strike/&gt;</c> in Word.</para>
    /// </summary>
    [Fact]
    public void Strikethrough_is_struck_and_not_bold()
    {
        var run = SingleRun("~~gone~~");

        run.Strike.Should().BeTrue();
        run.Bold.Should().BeFalse("`~~` is strikethrough — the doubled delimiter is not strength");
        run.Italic.Should().BeFalse();
    }

    /// <summary>
    /// The rest of the emphasis table, which the count-only test got wrong in four more ways:
    /// <c>++ins++</c> and <c>==mark==</c> came out bold, <c>~sub~</c> struck through, and
    /// <c>^sup^</c> italic. The model has no style for those four, so they render as plain text —
    /// their content is kept, their (unwritable) emphasis is not invented.
    /// </summary>
    [Theory]
    // markdown            bold   italic  strike
    [InlineData("**x**", true, false, false)]
    [InlineData("__x__", true, false, false)]
    [InlineData("*x*", false, true, false)]
    [InlineData("_x_", false, true, false)]
    [InlineData("~~x~~", false, false, true)]
    [InlineData("~x~", false, false, false)]   // subscript
    [InlineData("^x^", false, false, false)]   // superscript
    [InlineData("++x++", false, false, false)] // inserted
    [InlineData("==x==", false, false, false)] // marked
    public void Emphasis_is_keyed_on_the_delimiter_character(
        string markdown, bool bold, bool italic, bool strike)
    {
        var run = SingleRun(markdown);

        run.Text.Should().Be("x", "no emphasis flavour may swallow its own content");
        run.Bold.Should().Be(bold);
        run.Italic.Should().Be(italic);
        run.Strike.Should().Be(strike);
    }

    /// <summary>Triple asterisks still accumulate to bold+italic — nesting, not a special case.</summary>
    [Fact]
    public void Bold_italic_still_accumulates()
    {
        var run = SingleRun("***x***");

        run.Bold.Should().BeTrue();
        run.Italic.Should().BeTrue();
        run.Strike.Should().BeFalse();
    }

    /// <summary>Strikethrough inside genuine bold keeps both — the fix removes a phantom, not a style.</summary>
    [Fact]
    public void Strikethrough_nested_in_bold_keeps_both()
    {
        var doc = new DocumentBuilder().Build(
            "T", "**keep ~~gone~~**", new DocumentExportOptions(), BrandingOptions.Default);
        var runs = doc.Elements.OfType<ParagraphElement>().Single()
            .Content.OfType<TextInline>().ToArray();

        runs.Should().Contain(r => r.Text == "keep " && r.Bold && !r.Strike);
        runs.Should().Contain(r => r.Text == "gone" && r.Bold && r.Strike);
    }

    /// <summary>
    /// Two sections may share a title, and their anchors must not.
    ///
    /// <para>An HTML fragment reference resolves to the FIRST element carrying the id, so a
    /// duplicate made the second contents entry jump to the first section — in the rendered page
    /// and in the exported PDF's link annotation alike. It went unnoticed while the contents list
    /// printed no page numbers; reading those numbers back out of the print (#1309) exposed it as
    /// a set of destinations that ran backwards.</para>
    /// </summary>
    [Fact]
    public void Headings_that_share_a_title_still_get_distinct_anchors()
    {
        var doc = new DocumentBuilder().Build(
            "T",
            "# Introduction\n\n## Overview\n\n# Methods\n\n## Overview\n\n## Overview",
            new DocumentExportOptions(),
            BrandingOptions.Default);

        var anchors = doc.TocHeadings.Select(h => h.AnchorId).ToArray();

        anchors.Should().Equal("introduction", "overview", "methods", "overview-2", "overview-3");
        anchors.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Anchors_are_unique_across_chapters_too()
    {
        // Two chapters each opening with "Overview" is the ordinary case for a compiled document,
        // so the uniqueness scope has to be the document and not one chapter's walk.
        var doc = new DocumentBuilder().Build(
            "Book",
            [("Chapter 1", "# Overview"), ("Chapter 2", "# Overview")],
            new DocumentExportOptions { IncludeChildren = true },
            BrandingOptions.Default);

        doc.TocHeadings.Select(h => h.AnchorId).Should().Equal("overview", "overview-2");
    }

    [Fact]
    public void A_heading_with_no_slug_still_gets_a_usable_anchor()
    {
        // "###" or a heading of pure punctuation slugifies to nothing; an id of "" is one nothing
        // can link to, and two of them would collide with each other.
        var doc = new DocumentBuilder().Build(
            "T", "# ---\n\n# +++", new DocumentExportOptions(), BrandingOptions.Default);

        doc.TocHeadings.Select(h => h.AnchorId).Should().Equal("section", "section-2");
    }

    private static TextInline SingleRun(string markdown)
    {
        var doc = new DocumentBuilder().Build(
            "T", markdown, new DocumentExportOptions(), BrandingOptions.Default);
        return doc.Elements.OfType<ParagraphElement>().Single()
            .Content.OfType<TextInline>().Single();
    }
}
