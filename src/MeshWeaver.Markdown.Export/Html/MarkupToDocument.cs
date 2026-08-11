using System.Collections.Immutable;
using HtmlAgilityPack;
using MeshWeaver.Markdown.Export.Model;

namespace MeshWeaver.Markdown.Export.Html;

/// <summary>
/// Maps a resolved layout area's <see cref="MarkupNode"/> tree onto the export
/// <see cref="DocumentElement"/> model, so PDF and DOCX render an embedded area as REAL document
/// structure — a genuine table with genuine cells — rather than as text or a picture of one.
///
/// <para>This exists so there is exactly ONE walk of a live control tree. <see cref="AreaMarkupRenderer"/>
/// reads the area off its synchronization stream and produces a markup tree; that tree is then
/// either serialized to HTML (email / browser print) or mapped here to the document model
/// (QuestPDF / OpenXml). Adding a control means teaching the control renderer about it once, and
/// every export format follows. The alternative — a second control walk that emitted
/// <c>DocumentElement</c> directly — is precisely the duplication that let the PDF and pixel paths
/// drift apart in the first place.</para>
///
/// <para><b>Nested tables are flattened deliberately.</b> A card grid is a table of cards and each
/// card is itself a table, but <see cref="TableElement"/> models a cell as inline content, not as
/// arbitrary blocks — and neither QuestPDF's nor OpenXml's simple table path here renders a table
/// inside a cell. So a cell's whole subtree is flattened to inlines with line breaks at block
/// boundaries: the grid stays a grid, and each cell reads as the card's title, description and
/// link on separate lines. That is what a card grid looks like on paper.</para>
/// </summary>
public static class MarkupToDocument
{
    /// <summary>
    /// Tags that start a new line when their content is flattened into a run of inlines.
    /// </summary>
    private static readonly ImmutableHashSet<string> BlockTags =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "div", "p", "table", "tr", "td", "th", "section", "ul", "ol", "li",
            "h1", "h2", "h3", "h4", "h5", "h6", "blockquote");

    /// <summary>
    /// Converts a resolved area's markup tree into block elements ready to splice into a
    /// <see cref="Document"/>. Returns empty when the tree carries no content.
    /// </summary>
    public static ImmutableArray<DocumentElement> Convert(MarkupNode? node)
    {
        var builder = ImmutableArray.CreateBuilder<DocumentElement>();
        if (node is not null)
            ConvertBlock(node, builder);
        return builder.ToImmutable();
    }

    private static void ConvertBlock(MarkupNode node, ImmutableArray<DocumentElement>.Builder builder)
    {
        switch (node)
        {
            case MarkupFragmentNode fragment:
                foreach (var child in fragment.Children)
                    ConvertBlock(child, builder);
                break;

            case MarkupElement { Tag: var tag } element
                when tag.Equals("table", StringComparison.OrdinalIgnoreCase):
            {
                var table = ReadTable(element);
                if (table is not null)
                    builder.Add(table);
                break;
            }

            case MarkupElement element when IsBlock(element.Tag):
            {
                // A block element whose children are themselves blocks contributes those blocks;
                // one that holds inline content contributes a paragraph. Checking the children
                // rather than the tag keeps a <div> wrapping a table from collapsing into text.
                if (element.Children.Any(HasBlockContent))
                {
                    foreach (var child in element.Children)
                        ConvertBlock(child, builder);
                    break;
                }

                var inlines = Flatten(element);
                if (!inlines.IsEmpty)
                    builder.Add(new ParagraphElement(inlines));
                break;
            }

            default:
            {
                var inlines = Flatten(node);
                if (!inlines.IsEmpty)
                    builder.Add(new ParagraphElement(inlines));
                break;
            }
        }
    }

    /// <summary>
    /// Reads a markup table into a <see cref="TableElement"/>. Rows may sit directly under the
    /// table or inside a <c>thead</c>/<c>tbody</c>, so the walk descends through wrappers.
    /// </summary>
    private static TableElement? ReadTable(MarkupElement table)
    {
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<ImmutableArray<InlineElement>>>();
        var hasHeader = false;

        foreach (var row in DescendantRows(table))
        {
            var cells = ImmutableArray.CreateBuilder<ImmutableArray<InlineElement>>();
            foreach (var cell in row.Children.OfType<MarkupElement>())
            {
                if (!cell.Tag.Equals("td", StringComparison.OrdinalIgnoreCase)
                    && !cell.Tag.Equals("th", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (cell.Tag.Equals("th", StringComparison.OrdinalIgnoreCase))
                    hasHeader = true;
                cells.Add(Flatten(cell));
            }

            if (cells.Count > 0)
                rows.Add(cells.ToImmutable());
        }

        return rows.Count == 0 ? null : new TableElement(rows.ToImmutable(), hasHeader);
    }

    private static IEnumerable<MarkupElement> DescendantRows(MarkupElement table)
    {
        foreach (var child in table.Children.OfType<MarkupElement>())
        {
            if (child.Tag.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                yield return child;
                continue;
            }

            // thead / tbody / tfoot wrappers
            foreach (var nested in child.Children.OfType<MarkupElement>())
                if (nested.Tag.Equals("tr", StringComparison.OrdinalIgnoreCase))
                    yield return nested;
        }
    }

    private static bool HasBlockContent(MarkupNode node) => node switch
    {
        MarkupElement element => IsBlock(element.Tag),
        MarkupFragmentNode fragment => fragment.Children.Any(HasBlockContent),
        _ => false
    };

    private static bool IsBlock(string tag) => BlockTags.Contains(tag);

    /// <summary>
    /// Flattens a subtree to a run of inlines, inserting a line break at each block boundary so a
    /// card's title / description / link stay on separate lines inside one table cell.
    /// </summary>
    public static ImmutableArray<InlineElement> Flatten(MarkupNode node)
    {
        var builder = ImmutableArray.CreateBuilder<InlineElement>();
        FlattenInto(node, builder, bold: false, italic: false);
        TrimBreaks(builder);
        return builder.ToImmutable();
    }

    private static void FlattenInto(
        MarkupNode node, ImmutableArray<InlineElement>.Builder builder, bool bold, bool italic)
    {
        switch (node)
        {
            case MarkupTextNode text:
                if (!string.IsNullOrEmpty(text.Value))
                    builder.Add(new TextInline(text.Value, bold, italic, Strike: false));
                break;

            case MarkupRawNode raw:
                // Pre-rendered HTML (a MarkdownControl's body, an HtmlControl's data). The document
                // model has no HTML, so take the text the markup carries — parsed with a real DOM
                // rather than a regex, so a tag can never leak through as literal text.
                AppendRawText(raw.Html, builder, bold, italic);
                break;

            case MarkupFragmentNode fragment:
                foreach (var child in fragment.Children)
                    FlattenInto(child, builder, bold, italic);
                break;

            case MarkupElement element:
                FlattenElement(element, builder, bold, italic);
                break;
        }
    }

    private static void FlattenElement(
        MarkupElement element, ImmutableArray<InlineElement>.Builder builder, bool bold, bool italic)
    {
        if (element.Tag.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            AddBreak(builder);
            return;
        }

        if (element.Tag.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately dropped rather than approximated: neither content-fidelity renderer
            // draws an image (both emit bracketed alt text), so an area's decorative picture would
            // arrive as noise in the middle of a card.
            return;
        }

        // Emphasis arrives BOTH ways and both must be honoured. Markdown-authored emphasis comes as
        // <strong>/<em>; the control renderer's own emphasis comes as inline CSS, because inline
        // CSS is the only styling an email client obeys. Reading only the tags silently flattened
        // every card TITLE to body text — the grid printed correctly but read as one undifferentiated
        // block, which is visible the moment you look at a generated PDF rather than at its text.
        //
        // 🚨 Computed BEFORE the <a> branch: a card title is a STYLED LINK
        // (MarkupStyles.CardTitle carries font-weight:700), so an early return there would drop
        // exactly the emphasis this exists to preserve.
        var style = Attribute(element, "style");
        var isBold = bold
                     || element.Tag.Equals("b", StringComparison.OrdinalIgnoreCase)
                     || element.Tag.Equals("strong", StringComparison.OrdinalIgnoreCase)
                     || IsBoldStyle(style);
        var isItalic = italic
                       || element.Tag.Equals("i", StringComparison.OrdinalIgnoreCase)
                       || element.Tag.Equals("em", StringComparison.OrdinalIgnoreCase)
                       || style?.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase) == true;

        if (element.Tag.Equals("a", StringComparison.OrdinalIgnoreCase))
        {
            var href = Attribute(element, "href");
            var content = ImmutableArray.CreateBuilder<InlineElement>();
            foreach (var child in element.Children)
                FlattenInto(child, content, isBold, isItalic);
            TrimBreaks(content);
            if (content.Count > 0)
                builder.Add(new LinkInline(href ?? string.Empty, null, content.ToImmutable()));
            return;
        }

        var block = IsBlock(element.Tag);
        if (block)
            AddBreak(builder);

        foreach (var child in element.Children)
            FlattenInto(child, builder, isBold, isItalic);

        if (block)
            AddBreak(builder);
    }

    private static void AppendRawText(
        string html, ImmutableArray<InlineElement>.Builder builder, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(html))
            return;

        var document = new HtmlDocument();
        document.LoadHtml(html);
        var text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText) ?? string.Empty;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 0)
            builder.Add(new TextInline(text, bold, italic, Strike: false));
    }

    /// <summary>Adds a line break unless one (or nothing at all) is already there.</summary>
    private static void AddBreak(ImmutableArray<InlineElement>.Builder builder)
    {
        if (builder.Count == 0 || builder[^1] is LineBreakInline)
            return;
        builder.Add(new LineBreakInline());
    }

    private static void TrimBreaks(ImmutableArray<InlineElement>.Builder builder)
    {
        while (builder.Count > 0 && builder[^1] is LineBreakInline)
            builder.RemoveAt(builder.Count - 1);
        while (builder.Count > 0 && builder[0] is LineBreakInline)
            builder.RemoveAt(0);
    }

    /// <summary>
    /// Whether an inline <c>style</c> declares bold. Accepts the keyword and the numeric weights
    /// email markup actually uses (600–900); anything lighter is not bold.
    /// </summary>
    private static bool IsBoldStyle(string? style)
    {
        if (string.IsNullOrEmpty(style)) return false;
        var index = style.IndexOf("font-weight", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;

        var value = style[(index + "font-weight".Length)..].TrimStart(':', ' ');
        if (value.StartsWith("bold", StringComparison.OrdinalIgnoreCase)) return true;

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var weight) && weight >= 600;
    }

    private static string? Attribute(MarkupElement element, string name)
    {
        foreach (var (key, value) in element.Attributes)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }
}
