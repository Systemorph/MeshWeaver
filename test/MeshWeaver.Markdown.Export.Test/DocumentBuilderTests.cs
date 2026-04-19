using System.Linq;
using FluentAssertions;
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
}
