using System.Collections.Immutable;
using System.Globalization;
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
    private readonly string? _defaultNodePath;
    private readonly ImmutableDictionary<string, ImmutableArray<DocumentElement>> _resolvedAreas;

    /// <summary>
    /// One pipeline per node path. A pipeline is bound to the path relative embeds resolve
    /// against, so a document whose chapters come from different nodes needs one each; they are
    /// cached because building a Markdig pipeline per chapter is pure waste when (as usual) every
    /// chapter shares a path. Instance state, never static — see AGENTS.md → no static collections.
    /// </summary>
    private readonly Dictionary<string, MarkdownPipeline> _pipelines = new(StringComparer.Ordinal);

    /// <summary>The node path of the chapter currently being walked; keys resolved-area lookups.</summary>
    private string? _currentNodePath;

    /// <summary>
    /// Initializes a new instance of the <c>DocumentBuilder</c> class.
    /// </summary>
    /// <param name="currentNodePath">
    /// The exported node's own path, which is what makes a RELATIVE <c>@@("area:…")</c> embed
    /// resolvable. Null still parses embeds; only relative resolution is lost.
    /// </param>
    /// <param name="resolvedAreas">
    /// Embedded layout areas already resolved to document content, keyed as
    /// <see cref="Html.DocumentAreaResolution.KeyFor"/> forms them. Reading an area is reactive and
    /// cross-hub while this walk is synchronous, so resolution happens in a prior pass and the
    /// result is looked up here — the same split the export already uses for client-captured
    /// Mermaid/Math SVGs. Passing nothing renders embeds as a visible notice rather than silently
    /// dropping them.
    /// </param>
    public DocumentBuilder(
        string? currentNodePath = null,
        ImmutableDictionary<string, ImmutableArray<DocumentElement>>? resolvedAreas = null)
    {
        _defaultNodePath = currentNodePath;
        _currentNodePath = currentNodePath;
        _resolvedAreas = resolvedAreas ?? ImmutableDictionary<string, ImmutableArray<DocumentElement>>.Empty;
    }

    private MarkdownPipeline PipelineFor(string? nodePath)
    {
        var key = nodePath ?? string.Empty;
        if (!_pipelines.TryGetValue(key, out var pipeline))
            _pipelines[key] = pipeline = ExportMarkdownPipeline.For(nodePath);
        return pipeline;
    }

    /// <summary>
    /// Builds a document from a single markdown source.
    /// </summary>
    public Document Build(string title, string markdown, DocumentExportOptions options, BrandingOptions branding)
        => Build(title, [new ExportChapter(title, markdown, _defaultNodePath)], options, branding);

    /// <summary>
    /// Builds a document from one primary markdown + optional descendant markdowns. Each descendant
    /// becomes a chapter separated by <see cref="ChapterBreakElement"/> (and optionally a hard page break).
    /// </summary>
    public Document Build(
        string title,
        IEnumerable<(string ChapterTitle, string Markdown)> chapters,
        DocumentExportOptions options,
        BrandingOptions branding)
        => Build(
            title,
            chapters.Select(c => new ExportChapter(c.ChapterTitle, c.Markdown, _defaultNodePath)),
            options,
            branding);

    /// <summary>
    /// Builds a document from chapters that each carry their OWN node path.
    ///
    /// <para>Per-chapter paths matter: a relative embed resolves against the node it was written
    /// in, so with <c>IncludeChildren</c> every descendant chapter needs its own path or its
    /// embeds resolve against the root document's address instead.</para>
    /// </summary>
    public Document Build(
        string title,
        IEnumerable<ExportChapter> chapters,
        DocumentExportOptions options,
        BrandingOptions branding)
    {
        var mermaidIndex = 0;
        var mathIndex = 0;
        var elements = ImmutableArray.CreateBuilder<DocumentElement>();
        var tocHeadings = ImmutableArray.CreateBuilder<HeadingElement>();
        // Anchor ids must be unique across the WHOLE document, chapters included — two chapters
        // each opening with "Overview" is the ordinary case, not a corner one. Scoped to this
        // call, never to the instance, so two Build()s cannot suffix each other's ids.
        var anchors = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        var first = true;
        foreach (var chapter in chapters)
        {
            if (!first)
            {
                // Page break FIRST, then the chapter title — so each chapter (and each deck
                // slide) starts on a fresh page with its heading at the top, never orphaned
                // at the foot of the previous chapter's last page.
                if (options.PageBreakBetweenChildren)
                    elements.Add(new PageBreakElement());
                elements.Add(new ChapterBreakElement(chapter.Title));
            }
            first = false;

            _currentNodePath = chapter.NodePath ?? _defaultNodePath;
            var doc = Markdig.Markdown.Parse(chapter.Markdown, PipelineFor(_currentNodePath));
            WalkBlocks(doc, options, elements, tocHeadings, anchors, ref mermaidIndex, ref mathIndex);
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
        ImmutableHashSet<string>.Builder anchors,
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
                    var anchor = AnchorFromInlines(content, anchors);
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
                    WalkBlocks(q, options, inner, tocHeadings, anchors, ref mermaidIndex, ref mathIndex);
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
                            WalkBlocks(li, options, content, tocHeadings, anchors, ref mermaidIndex, ref mathIndex);
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
                // 🚨 MUST precede the generic ContainerBlock case: LayoutAreaComponentInfo IS a
                // ContainerBlock, so falling through would walk an embed's (empty) children and
                // emit nothing at all — a silent hole exactly where the author put a view.
                case LayoutAreaComponentInfo areaInfo:
                    elements.AddRange(ResolveArea(areaInfo));
                    break;
                case ContainerBlock container2:
                    WalkBlocks(container2, options, elements, tocHeadings, anchors, ref mermaidIndex, ref mathIndex);
                    break;
            }
        }
    }

    /// <summary>
    /// Substitutes an embedded layout area with the content resolved for it beforehand.
    ///
    /// <para>When no entry exists the embed becomes a VISIBLE notice naming the area, never
    /// nothing: a missing entry means the resolution pass did not run for this document, and a
    /// document that silently omits a section its author embedded is the defect this whole change
    /// exists to remove. (Areas that ran but produced nothing already carry a localized notice
    /// placed by <see cref="Html.DocumentAreaResolution"/>; this fallback covers the caller that
    /// never resolved at all, so it deliberately stays free of hub/localization dependencies.)</para>
    /// </summary>
    private ImmutableArray<DocumentElement> ResolveArea(LayoutAreaComponentInfo info)
    {
        var key = Html.DocumentAreaResolution.KeyFor(_currentNodePath, info);
        if (_resolvedAreas.TryGetValue(key, out var resolved) && !resolved.IsEmpty)
            return resolved;

        var label = info.Area ?? info.RawPath ?? string.Empty;
        return
        [
            new ParagraphElement(
                ImmutableArray.Create<InlineElement>(
                    new TextInline($"[{label}]", Bold: false, Italic: true, Strike: false)))
        ];
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
                {
                    var (emBold, emItalic, emStrike) = EmphasisStyle(em);
                    ReadInlinesInto(em, builder, bold || emBold, italic || emItalic, strike || emStrike);
                    break;
                }
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

    /// <summary>
    /// Which of the document model's styles an emphasis span carries.
    ///
    /// <para>🚨 The delimiter CHARACTER decides — never the count on its own. Markdig's
    /// <c>EmphasisExtras</c> (enabled here by <c>UseAdvancedExtensions</c>) folds SIX different
    /// constructs into the one <see cref="EmphasisInline"/> node and tells them apart by
    /// <see cref="EmphasisInline.DelimiterChar"/>: <c>**</c>/<c>__</c> strong, <c>*</c>/<c>_</c>
    /// emphasis, <c>~~</c> strikethrough, <c>~</c> subscript, <c>^</c> superscript, <c>++</c>
    /// inserted, <c>==</c> marked.</para>
    ///
    /// <para>Reading strength off <c>DelimiterCount >= 2</c> alone therefore made EVERY doubled
    /// delimiter strong, and <c>~~strike~~</c> is doubled: it printed as <b>bold</b> AND struck
    /// through in both PDF and DOCX (#1373). The same slip gave <c>++ins++</c> and <c>==mark==</c>
    /// bold, <c>~sub~</c> a strikethrough, and <c>^sup^</c> italics — every one of them an emphasis
    /// its author never wrote.</para>
    ///
    /// <para>Subscript, superscript, inserted and marked have no representation in the document
    /// model, which carries bold / italic / strike / code and nothing else. They render as their
    /// plain text: no content is lost, and a construct the model cannot express is better shown
    /// neutrally than dressed in the wrong style. Giving them styles of their own is a change to
    /// the model, not a fix to this.</para>
    /// </summary>
    private static (bool Bold, bool Italic, bool Strike) EmphasisStyle(EmphasisInline em) =>
        em.DelimiterChar switch
        {
            // `***both***` arrives as a count-1 span wrapping a count-2 span, so each level maps on
            // its own and the two accumulate into bold+italic exactly as before.
            '*' or '_' => (em.DelimiterCount >= 2, em.DelimiterCount == 1, false),
            // A SINGLE '~' is subscript, not a strikethrough — the count matters here too, just not
            // as a stand-in for strength.
            '~' => (false, false, em.DelimiterCount >= 2),
            _ => (false, false, false),
        };

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

    /// <summary>
    /// The id a heading carries and its contents entry links to — slugified from the heading text
    /// and then made UNIQUE within the document.
    ///
    /// <para>🚨 Uniqueness is not cosmetic. Two sections legitimately called "Overview" slugify to
    /// the same <c>overview</c>, and an HTML fragment reference resolves to the FIRST element with
    /// that id — so the second entry's link jumped to the first section, and the PDF link
    /// annotation recorded that wrong destination too. It was invisible while the contents list
    /// printed no page numbers; the page-number read-back (#1309) refuses a document whose
    /// destinations run backwards, which is how it surfaced. Disambiguating with a numeric suffix
    /// is what every markdown renderer does, so an author's expectation is unchanged.</para>
    /// </summary>
    private static string AnchorFromInlines(
        ImmutableArray<InlineElement> inlines,
        ImmutableHashSet<string>.Builder taken)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in inlines)
        {
            if (i is TextInline t) sb.Append(t.Text);
            else if (i is ModelLinkInline l)
                foreach (var c in l.Content)
                    if (c is TextInline tt) sb.Append(tt.Text);
        }

        var slug = Slugify(sb.ToString());
        // An empty heading (or one made only of punctuation) has no slug to make unique; give it
        // a stable generated one rather than an id of "" that nothing can link to.
        if (slug.Length == 0)
            slug = "section";

        var candidate = slug;
        for (var suffix = 2; !taken.Add(candidate); suffix++)
            candidate = slug + "-" + suffix.ToString(CultureInfo.InvariantCulture);

        return candidate;
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
