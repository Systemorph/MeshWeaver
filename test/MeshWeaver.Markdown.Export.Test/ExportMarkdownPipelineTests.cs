using System.Collections.Immutable;
using System.Linq;
using Markdig.Syntax;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Export.Ast;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Model;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// Pins the ROOT CAUSE of the lost-embeds defect at the smallest possible scope.
///
/// <para>The export used to parse with its own Markdig pipeline —
/// <c>UseAdvancedExtensions().UsePageBreaks()</c> — which omitted
/// <c>LayoutAreaMarkdownExtension</c>. An <c>@@(…)</c> embed was therefore not a block at all: it
/// parsed as an ordinary paragraph and the renderer faithfully printed the author's markdown
/// source into the PDF. No error, no warning, no test. These tests fail the moment that pipeline
/// stops understanding embeds again, without needing a mesh, a browser or a rendered file.</para>
/// </summary>
public class ExportMarkdownPipelineTests
{
    private static readonly BrandingOptions Branding = new();

    private static DocumentExportOptions Options => new()
    {
        Format = ExportFormat.Pdf,
        CoverPage = false,
        TableOfContents = false
    };

    [Theory]
    // The exact form the reported document uses: the `area:` keyword with a query string.
    [InlineData("@@(\"area:OgCard?urls=https://example.com/a,https://example.com/b\")", "OgCard")]
    // The slash form, and the node-scoped form.
    [InlineData("@@(\"Some/Doc/area/OgCard/Some/Target\")", "OgCard")]
    [InlineData("@@(\"area:Search\")", "Search")]
    public void ExportPipeline_RecognisesAnEmbed_AsALayoutAreaBlock(string markdown, string expectedArea)
    {
        var document = Markdig.Markdown.Parse(markdown, ExportMarkdownPipeline.For("Some/Doc"));

        var embed = document.Descendants<LayoutAreaComponentInfo>().Should().ContainSingle(
            "the export pipeline must parse an embed as a layout area, not as prose — omitting "
            + "LayoutAreaMarkdownExtension is what made every PDF print the embed's source text")
            .Which;

        embed.Area.Should().Be(expectedArea);
    }

    [Fact]
    public void AnEmbed_NeverRendersAsItsOwnSourceText()
    {
        // The regression, stated as directly as it can be: whatever else happens to an embed, the
        // author's `@@(...)` must not survive into the document as readable text.
        const string markdown = "# Report\n\n@@(\"area:OgCard?urls=https://example.com/a\")\n";

        var document = new DocumentBuilder("Some/Doc")
            .Build("Report", markdown, Options, Branding);

        AllText(document).Should().NotContain("@@(",
            "an embed must never be printed as its own markdown source");
    }

    [Fact]
    public void WithNoResolvedContent_AnEmbed_BecomesVisible_NotABlank()
    {
        // Nothing was resolved for this build, which is the "resolution pass did not run" case.
        // It must still leave something a reader can see: a document that silently drops a section
        // its author placed is exactly the defect being fixed, and a blank is indistinguishable
        // from the author never having embedded anything.
        const string markdown = "# Report\n\n@@(\"area:RevenueByRegion\")\n";

        var document = new DocumentBuilder("Some/Doc")
            .Build("Report", markdown, Options, Branding);

        AllText(document).Should().Contain("RevenueByRegion",
            "an unresolved embed must name itself so the gap is visible and traceable");
    }

    [Fact]
    public void ResolvedContent_IsSplicedInPlaceOfTheEmbed()
    {
        const string markdown = "# Report\n\nBefore.\n\n@@(\"area:RevenueByRegion\")\n\nAfter.\n";

        // Key the entry the way the RESOLVER keys it, rather than restating the format here — the
        // two agreeing is the contract, and DocumentAreaResolution.KeyFor is the one definition of
        // it. (That the two really do agree end-to-end is proven by the integration test, which
        // resolves and renders through the real script templates.)
        var embed = Markdig.Markdown
            .Parse(markdown, ExportMarkdownPipeline.For("Some/Doc"))
            .Descendants<LayoutAreaComponentInfo>()
            .Single();

        var resolved = ImmutableDictionary<string, ImmutableArray<DocumentElement>>.Empty
            .Add(MeshWeaver.Markdown.Export.Html.DocumentAreaResolution.KeyFor("Some/Doc", embed),
            [
                new ParagraphElement([new TextInline("RESOLVEDCONTENT", false, false, false)])
            ]);

        var document = new DocumentBuilder("Some/Doc", resolved)
            .Build("Report", markdown, Options, Branding);

        var text = AllText(document);
        text.Should().Contain("RESOLVEDCONTENT", "the resolved area replaces the embed");
        text.Should().NotContain("@@(");

        // Position matters: the area belongs where the author put it, between the two paragraphs.
        text.IndexOf("Before.", System.StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("RESOLVEDCONTENT", System.StringComparison.Ordinal));
        text.IndexOf("RESOLVEDCONTENT", System.StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("After.", System.StringComparison.Ordinal));
    }

    [Fact]
    public void PageBreakMarkers_StillWork_AfterThePipelineChange()
    {
        // The export pipeline gained the layout-area extension; it must not have LOST the export's
        // own page-break extension in the process. `\newpage` is an export-only feature that the
        // portal's pipeline does not provide, so nothing else would catch its disappearance.
        const string markdown = "# One\n\n\\newpage\n\n# Two\n";

        var document = new DocumentBuilder()
            .Build("Doc", markdown, Options with { PageBreakBeforeH1 = false }, Branding);

        document.Elements.OfType<PageBreakElement>().Should().ContainSingle();
    }

    /// <summary>Flattens every string the document model carries, for order-sensitive assertions.</summary>
    private static string AllText(Document document) =>
        string.Join("\n", document.Elements.Select(ElementText));

    private static string ElementText(DocumentElement element) => element switch
    {
        ParagraphElement p => InlineText(p.Content),
        HeadingElement h => InlineText(h.Content),
        CodeBlockElement c => c.Source,
        TableElement t => string.Join(" ", t.Rows.SelectMany(r => r.Select(InlineText))),
        BlockQuoteElement q => string.Join("\n", q.Content.Select(ElementText)),
        ListElement l => string.Join("\n", l.Items.SelectMany(i => i.Content.Select(ElementText))),
        ChapterBreakElement cb => cb.Title,
        _ => string.Empty
    };

    private static string InlineText(ImmutableArray<InlineElement> inlines) =>
        string.Concat(inlines.Select(i => i switch
        {
            TextInline t => t.Text,
            LinkInline l => InlineText(l.Content),
            ImageInline img => img.Alt ?? string.Empty,
            LineBreakInline => "\n",
            _ => string.Empty
        }));
}
