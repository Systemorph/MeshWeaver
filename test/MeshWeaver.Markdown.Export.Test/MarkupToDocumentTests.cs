using System.Linq;
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Model;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// Pins how a resolved layout area's markup becomes PDF/DOCX document structure.
///
/// <para>This is the piece that decides whether an embedded card grid ARRIVES as a grid on paper
/// or as a paragraph of run-together text. It is asserted here rather than only through a rendered
/// PDF because the shape is what matters and a byte-level assertion cannot express it: a real
/// <see cref="TableElement"/> is what makes QuestPDF draw columns and Word emit <c>w:tbl</c>, which
/// in turn is what survives the reader repaginating or editing the file.</para>
/// </summary>
public class MarkupToDocumentTests
{
    private const int Columns = 2;

    /// <summary>
    /// Builds the shape AreaMarkupRenderer emits for a grid of link-preview cards — INCLUDING the
    /// empty padding cells it appends to a short final row. Reproducing the padding matters: the
    /// mapper's job is to carry the renderer's column count through, and a helper that silently
    /// dropped the padding would let a mapper that loses it still look correct.
    /// </summary>
    private static MarkupNode CardGrid(params (string Title, string Description, string Href)[] cards)
    {
        var rows = cards
            .Chunk(Columns)
            .Select(chunk =>
            {
                var cells = chunk
                    .Select(card => (MarkupNode)MarkupNode.El("td")
                        .With("width", "50%")
                        .Add(MarkupNode.El("table").Add(
                            MarkupNode.El("tr").Add(
                                MarkupNode.El("td")
                                    .Add(MarkupNode.El("a").With("href", card.Href)
                                        .Style(MarkupStyles.CardTitle)
                                        .Add(MarkupNode.Text(card.Title)))
                                    .Add(MarkupNode.El("div").Style(MarkupStyles.CardDescription)
                                        .Add(MarkupNode.Text(card.Description)))
                                    .Add(MarkupNode.El("div").Add(
                                        MarkupNode.El("a").With("href", card.Href)
                                            .Style(MarkupStyles.CardLink)
                                            .Add(MarkupNode.Text(card.Href))))))))
                    .ToList();

                // The renderer pads a short final row with EMPTY cells so every row keeps the same
                // column widths; reproduce that here so the mapper is tested against what it
                // actually receives.
                while (cells.Count < Columns)
                    cells.Add(MarkupNode.El("td").With("width", "50%"));

                return (MarkupNode)MarkupNode.El("tr").Add(cells);
            });

        return MarkupNode.El("table").Add(rows);
    }

    [Fact]
    public void CardGrid_BecomesARealTable_WithOneCellPerCard()
    {
        var elements = MarkupToDocument.Convert(CardGrid(
            ("Underwriting", "How risk is priced.", "https://portal.example.com/Underwriting"),
            ("Pricing", "Curve fitting.", "https://portal.example.com/Pricing"),
            ("Claims", "Settlement flow.", "https://portal.example.com/Claims"),
            ("Reserving", "Triangles.", "https://portal.example.com/Reserving")));

        var table = elements.Should().ContainSingle().Which
            .Should().BeOfType<TableElement>().Which;

        table.Rows.Should().HaveCount(2, "four cards at two per row");
        table.Rows.Should().AllSatisfy(row =>
            row.Should().HaveCount(2, "each row keeps both columns so the grid stays aligned"));
    }

    [Fact]
    public void EachCardCell_KeepsTitleDescriptionAndLinkOnSeparateLines()
    {
        var elements = MarkupToDocument.Convert(CardGrid(
            ("Underwriting", "How risk is priced.", "https://portal.example.com/Underwriting")));

        var cell = elements.OfType<TableElement>().Single().Rows[0][0];

        // The nested card table is FLATTENED into the cell, because a table inside a table cell is
        // not something the document model (or either renderer's simple table path) can express.
        // What must survive that flattening is the card's reading order and its line structure.
        var text = string.Concat(cell.OfType<TextInline>().Select(t => t.Text));
        var linkText = string.Concat(cell.OfType<LinkInline>()
            .SelectMany(l => l.Content.OfType<TextInline>()).Select(t => t.Text));

        (text + linkText).Should().Contain("Underwriting");
        (text + linkText).Should().Contain("How risk is priced.");

        cell.OfType<LineBreakInline>().Should().NotBeEmpty(
            "title, description and link must not run together into one line of text");

        // The card is CLICKABLE in the exported file — the href survives as a real link, so the
        // PDF/Word reader can follow it after the file has been mailed on.
        cell.OfType<LinkInline>().Should()
            .Contain(l => l.Url == "https://portal.example.com/Underwriting");
    }

    /// <summary>
    /// The card TITLE must arrive bold, so a card reads as a card rather than as three
    /// indistinguishable lines of body text.
    ///
    /// <para>Found by generating a PDF and looking at it, not by an assertion: the grid was
    /// structurally perfect and visually flat. The control renderer carries emphasis as inline CSS
    /// (<c>font-weight:700</c>) because that is the only styling a mail client obeys — never as
    /// <c>&lt;strong&gt;</c> — and the title is a styled <c>&lt;a&gt;</c>, so a mapper that reads
    /// only tags, or that handles links before reading style, drops it.</para>
    /// </summary>
    [Fact]
    public void CardTitle_ArrivesBold_ThoughItsEmphasisIsInlineCssOnALink()
    {
        var card = MarkupNode.El("td")
            .Add(MarkupNode.El("a").With("href", "https://portal.example.com/X")
                .Style(MarkupStyles.CardTitle)
                .Add(MarkupNode.Text("Underwriting")))
            .Add(MarkupNode.El("div").Style(MarkupStyles.CardDescription)
                .Add(MarkupNode.Text("How risk is priced.")));

        var inlines = MarkupToDocument.Flatten(card);

        var title = inlines.OfType<LinkInline>()
            .SelectMany(l => l.Content.OfType<TextInline>())
            .Single(t => t.Text == "Underwriting");
        title.Bold.Should().BeTrue("MarkupStyles.CardTitle declares font-weight:700");

        inlines.OfType<TextInline>().Single(t => t.Text.StartsWith("How risk"))
            .Bold.Should().BeFalse("the description is body text and must stay unemphasised");
    }

    [Theory]
    [InlineData("font-weight:700", true)]
    [InlineData("font-weight:bold", true)]
    [InlineData("font-weight: 600", true)]
    [InlineData("font-weight:400", false)]
    [InlineData("color:#333", false)]
    public void InlineCssBoldIsRecognised_ForTheWeightsEmailMarkupActuallyUses(string css, bool bold)
    {
        var inlines = MarkupToDocument.Flatten(
            MarkupNode.El("span").Style(css).Add(MarkupNode.Text("x")));

        inlines.OfType<TextInline>().Single().Bold.Should().Be(bold);
    }

    /// <summary>
    /// A short final row keeps its padding cell, so every row has the SAME number of columns.
    ///
    /// <para>This matters to the renderers, not just to tidiness: QuestPDF and Word derive the
    /// column layout from the row's cell count, so a final row with fewer cells makes its one card
    /// stretch the full width and the grid visibly loses alignment on the last row.</para>
    /// </summary>
    [Fact]
    public void ShortFinalRow_KeepsItsPaddingCell_SoEveryRowHasTheSameColumnCount()
    {
        var elements = MarkupToDocument.Convert(CardGrid(
            ("A", "first", "https://portal.example.com/A"),
            ("B", "second", "https://portal.example.com/B"),
            ("C", "third", "https://portal.example.com/C")));

        var table = elements.OfType<TableElement>().Single();
        table.Rows.Should().HaveCount(2);
        table.Rows.Should().AllSatisfy(row => row.Should().HaveCount(Columns,
            "an empty padding cell is still a cell — dropping it would collapse the last row"));
        table.Rows[1][1].Should().BeEmpty("the padding cell carries no content");
    }

    [Fact]
    public void PlainTextArea_BecomesAParagraph_NotATable()
    {
        var elements = MarkupToDocument.Convert(
            MarkupNode.El("p").Add(MarkupNode.Text("Just a sentence.")));

        elements.Should().ContainSingle().Which
            .Should().BeOfType<ParagraphElement>().Which
            .Content.OfType<TextInline>().Single().Text.Should().Be("Just a sentence.");
    }

    [Fact]
    public void PreRenderedHtml_ContributesItsText_NeverItsTags()
    {
        // A MarkdownControl inside an area arrives as pre-rendered HTML. The document model has no
        // HTML, so it must contribute TEXT — and a tag must never leak through as literal
        // characters, which is what a regex-based strip would eventually do.
        var elements = MarkupToDocument.Convert(
            MarkupNode.Raw("<p>Hello <strong>world</strong> &amp; welcome</p>"));

        var text = string.Concat(elements.OfType<ParagraphElement>()
            .SelectMany(p => p.Content.OfType<TextInline>()).Select(t => t.Text));

        text.Should().Contain("Hello");
        text.Should().Contain("world");
        text.Should().Contain("&", "entities must be decoded, not passed through as &amp;");
        text.Should().NotContain("<strong>");
        text.Should().NotContain("&amp;");
    }

    [Fact]
    public void EmptyArea_ConvertsToNothing_SoTheCallerCanSubstituteItsOwnNotice()
    {
        // Convert must not invent a placeholder itself: the "area produced nothing" decision (and
        // its localized wording) belongs to the caller, which knows the output format.
        MarkupToDocument.Convert(null).Should().BeEmpty();
        MarkupToDocument.Convert(MarkupNode.Empty).Should().BeEmpty();
    }
}
