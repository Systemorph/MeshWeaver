using System.Collections.Immutable;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Markdown.Export.Model;
using MdLineBreakInline = Markdig.Syntax.Inlines.LineBreakInline;
using MdLinkInline = Markdig.Syntax.Inlines.LinkInline;
using ModelLineBreakInline = MeshWeaver.Markdown.Export.Model.LineBreakInline;
using ModelLinkInline = MeshWeaver.Markdown.Export.Model.LinkInline;

namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// Walks a Markdig AST and produces a flat <see cref="Document"/> model ready for rendering.
/// Applies page-break rules and captures Mermaid / Math block indexes for SVG substitution.
/// </summary>
public class DocumentBuilder
{
    private readonly MarkdownPipeline _pipeline;

    public DocumentBuilder()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePageBreaks()
            .Build();
    }

    /// <summary>
    /// Builds a document from a single markdown source.
    /// </summary>
    public Document Build(string title, string markdown, DocumentExportOptions options, BrandingOptions branding)
        => Build(title, new[] { (title, markdown) }, options, branding);

    /// <summary>
    /// Builds a document from one primary markdown + optional descendant markdowns. Each descendant
    /// becomes a chapter separated by <see cref="ChapterBreakElement"/> (and optionally a hard page break).
    /// </summary>
    public Document Build(
        string title,
        IEnumerable<(string ChapterTitle, string Markdown)> chapters,
        DocumentExportOptions options,
        BrandingOptions branding)
    {
        var mermaidIndex = 0;
        var mathIndex = 0;
        var elements = ImmutableArray.CreateBuilder<DocumentElement>();
        var tocHeadings = ImmutableArray.CreateBuilder<HeadingElement>();

        var first = true;
        foreach (var (chapterTitle, markdown) in chapters)
        {
            if (!first)
            {
                elements.Add(new ChapterBreakElement(chapterTitle));
                if (options.PageBreakBetweenChildren)
                    elements.Add(new PageBreakElement());
            }
            first = false;

            var doc = Markdig.Markdown.Parse(markdown, _pipeline);
            WalkBlocks(doc, options, elements, tocHeadings, ref mermaidIndex, ref mathIndex);
        }

        return new Document(
            Title: title,
            Branding: branding,
            Options: options,
            Elements: elements.ToImmutable(),
            TocHeadings: tocHeadings.ToImmutable());
    }

    private void WalkBlocks(
        ContainerBlock container,
        DocumentExportOptions options,
        ImmutableArray<DocumentElement>.Builder elements,
        ImmutableArray<HeadingElement>.Builder tocHeadings,
        ref int mermaidIndex,
        ref int mathIndex)
    {
        var sawHeading1 = false;
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock h:
                {
                    if (h.Level == 1 && options.PageBreakBeforeH1 && sawHeading1)
                        elements.Add(new PageBreakElement());
                    if (h.Level == 2 && options.PageBreakBeforeH2)
                        elements.Add(new PageBreakElement());

                    var content = ReadInlines(h.Inline);
                    var anchor = AnchorFromInlines(content);
                    var heading = new HeadingElement(h.Level, anchor, content);
                    elements.Add(heading);
                    if (h.Level is >= 1 and <= 3) tocHeadings.Add(heading);
                    if (h.Level == 1) sawHeading1 = true;
                    break;
                }
                case ParagraphBlock p:
                    elements.Add(new ParagraphElement(ReadInlines(p.Inline)));
                    break;
                case FencedCodeBlock fcb:
                {
                    var lang = fcb.Info?.Trim();
                    var source = fcb.Lines.ToString();
                    if (string.Equals(lang, "mermaid", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = mermaidIndex++;
                        options.RenderedSvgs.TryGetValue($"mermaid:{idx}", out var svg);
                        elements.Add(new MermaidElement(idx, source, svg));
                    }
                    else if (string.Equals(lang, "math", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = mathIndex++;
                        options.RenderedSvgs.TryGetValue($"math:{idx}", out var svg);
                        elements.Add(new MathElement(idx, source, svg));
                    }
                    else
                    {
                        elements.Add(new CodeBlockElement(lang, source));
                    }
                    break;
                }
                case CodeBlock cb:
                    elements.Add(new CodeBlockElement(null, cb.Lines.ToString()));
                    break;
                case ThematicBreakBlock:
                    elements.Add(new HorizontalRuleElement());
                    break;
                case PageBreakBlock:
                    elements.Add(new PageBreakElement());
                    break;
                case QuoteBlock q:
                {
                    var inner = ImmutableArray.CreateBuilder<DocumentElement>();
                    WalkBlocks(q, options, inner, tocHeadings, ref mermaidIndex, ref mathIndex);
                    elements.Add(new BlockQuoteElement(inner.ToImmutable()));
                    break;
                }
                case ListBlock list:
                {
                    var items = ImmutableArray.CreateBuilder<ListItemElement>();
                    foreach (var child in list)
                    {
                        if (child is ListItemBlock li)
                        {
                            var content = ImmutableArray.CreateBuilder<DocumentElement>();
                            WalkBlocks(li, options, content, tocHeadings, ref mermaidIndex, ref mathIndex);
                            items.Add(new ListItemElement(content.ToImmutable()));
                        }
                    }
                    elements.Add(new ListElement(list.IsOrdered, items.ToImmutable()));
                    break;
                }
                case Table table:
                    elements.Add(ReadTable(table));
                    break;
                case HtmlBlock html:
                    // Render raw HTML as a code block fallback — pure C# can't faithfully reproduce HTML.
                    elements.Add(new CodeBlockElement("html", html.Lines.ToString()));
                    break;
                case ContainerBlock container2:
                    WalkBlocks(container2, options, elements, tocHeadings, ref mermaidIndex, ref mathIndex);
                    break;
            }
        }
    }

    private static TableElement ReadTable(Table table)
    {
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<ImmutableArray<InlineElement>>>();
        var hasHeader = false;
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            if (row.IsHeader) hasHeader = true;
            var cells = ImmutableArray.CreateBuilder<ImmutableArray<InlineElement>>();
            foreach (var cellObj in row)
            {
                if (cellObj is TableCell cell)
                {
                    var inlines = ImmutableArray.CreateBuilder<InlineElement>();
                    foreach (var b in cell)
                    {
                        if (b is ParagraphBlock p)
                            inlines.AddRange(ReadInlines(p.Inline));
                    }
                    cells.Add(inlines.ToImmutable());
                }
            }
            rows.Add(cells.ToImmutable());
        }
        return new TableElement(rows.ToImmutable(), hasHeader);
    }

    private static ImmutableArray<InlineElement> ReadInlines(ContainerInline? container)
    {
        if (container is null) return ImmutableArray<InlineElement>.Empty;
        var builder = ImmutableArray.CreateBuilder<InlineElement>();
        ReadInlinesInto(container, builder, bold: false, italic: false, strike: false);
        return builder.ToImmutable();
    }

    private static void ReadInlinesInto(
        ContainerInline container,
        ImmutableArray<InlineElement>.Builder builder,
        bool bold, bool italic, bool strike)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    builder.Add(new TextInline(lit.Content.ToString(), bold, italic, strike));
                    break;
                case EmphasisInline em:
                    var isStrong = em.DelimiterCount >= 2;
                    var isStrike = em.DelimiterChar is '~';
                    ReadInlinesInto(em, builder,
                        bold || isStrong,
                        italic || (!isStrong && !isStrike),
                        strike || isStrike);
                    break;
                case CodeInline code:
                    builder.Add(new TextInline(code.Content, bold, italic, strike, Code: true));
                    break;
                case MdLineBreakInline:
                    builder.Add(new ModelLineBreakInline());
                    break;
                case MdLinkInline link when !link.IsImage:
                {
                    var inner = ImmutableArray.CreateBuilder<InlineElement>();
                    ReadInlinesInto(link, inner, bold, italic, strike);
                    builder.Add(new ModelLinkInline(link.Url ?? "", link.Title, inner.ToImmutable()));
                    break;
                }
                case MdLinkInline imgLink when imgLink.IsImage:
                {
                    var alt = ExtractPlainText(imgLink);
                    builder.Add(new ImageInline(imgLink.Url ?? "", alt, imgLink.Title));
                    break;
                }
                case AutolinkInline auto:
                {
                    var content = ImmutableArray.Create<InlineElement>(new TextInline(auto.Url, bold, italic, strike));
                    builder.Add(new ModelLinkInline(auto.Url, null, content));
                    break;
                }
                case HtmlInline html:
                    // Skip raw HTML inline tags — output the literal text around them.
                    builder.Add(new TextInline(html.Tag, bold, italic, strike));
                    break;
                case ContainerInline nested:
                    ReadInlinesInto(nested, builder, bold, italic, strike);
                    break;
            }
        }
    }

    private static string ExtractPlainText(ContainerInline c)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = c.FirstChild; i is not null; i = i.NextSibling)
        {
            if (i is LiteralInline l) sb.Append(l.Content.ToString());
            else if (i is ContainerInline nested) sb.Append(ExtractPlainText(nested));
        }
        return sb.ToString();
    }

    private static string AnchorFromInlines(ImmutableArray<InlineElement> inlines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in inlines)
        {
            if (i is TextInline t) sb.Append(t.Text);
            else if (i is ModelLinkInline l)
                foreach (var c in l.Content)
                    if (c is TextInline tt) sb.Append(tt.Text);
        }
        return Slugify(sb.ToString());
    }

    private static string Slugify(string s)
    {
        var lowered = s.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lowered.Length);
        var prevDash = false;
        foreach (var c in lowered)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); prevDash = false; }
            else if (!prevDash && sb.Length > 0) { sb.Append('-'); prevDash = true; }
        }
        return sb.ToString().TrimEnd('-');
    }
}
