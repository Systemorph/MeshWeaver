using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Html;
using MeshWeaver.Markdown.Export.Model;
using Document = MeshWeaver.Markdown.Export.Model.Document;

namespace MeshWeaver.Markdown.Export.Pdf;

/// <summary>
/// Composes a <see cref="Document"/> into ONE self-contained HTML document for the headless
/// browser to print — the content-faithful counterpart to <c>SlidePrintComposer</c>.
///
/// <para><b>This class carries the page furniture.</b> Cover page, table of contents, running
/// header, running footer with page numbers and the page-break rules are all decided here, in
/// markup and CSS, rather than by a PDF document model. The browser is the layout engine; this
/// is the document it lays out.</para>
///
/// <para>Pure and synchronous by design: no hub, no IO, no browser. Every parity question —
/// "does the cover appear", "is the header suppressed when blank", "does an H1 start a page" —
/// is therefore answerable by an ordinary unit test with nothing installed.</para>
///
/// <para><b>Markup is built as a <see cref="MarkupNode"/> tree, never by string interpolation.</b>
/// That is the one place in this assembly that turns a tree into HTML, and it escapes text and
/// attribute values on the single path out. The CSS is a template file whose few interpolated
/// values are escaped here by <see cref="CssString"/> / <see cref="CssColor"/> — branding text is
/// authored in a mesh node and lands inside a <c>&lt;style&gt;</c> element, so it is untrusted
/// input in a context HTML encoding does not cover.</para>
/// </summary>
public static partial class DocumentPrintComposer
{
    private const string TitleToken = "{{TITLE}}";
    private const string StylesToken = "{{STYLES}}";
    private const string CoverToken = "{{COVER}}";
    private const string TocToken = "{{TOC}}";
    private const string BodyToken = "{{BODY}}";

    private const string PageSizeToken = "{{PAGE_SIZE}}";
    private const string PrimaryToken = "{{PRIMARY}}";
    private const string AccentToken = "{{ACCENT}}";
    private const string FontFamilyToken = "{{FONT_FAMILY}}";
    private const string HeaderTextToken = "{{HEADER_TEXT}}";
    private const string HeaderLogoToken = "{{HEADER_LOGO}}";
    private const string HeaderRuleToken = "{{HEADER_RULE}}";
    private const string FooterTextToken = "{{FOOTER_TEXT}}";

    /// <summary>
    /// The contents heading. Deliberately the same hard-coded English string the document-model
    /// renderers have always written (<c>DocxDocumentRenderer</c> writes it too) — this migration
    /// changes the renderer, not the copy. Localizing it is a separate change that has to reach
    /// both renderers and thread the exporting user's locale into the export script.
    /// </summary>
    internal const string ContentsHeading = "Contents";

    /// <summary>Composes the print document for <paramref name="document"/>.</summary>
    /// <returns>A complete, self-contained HTML document.</returns>
    public static string Compose(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Fill(DocumentPrintTemplates.Document,
            (TitleToken, MarkupNode.Text(document.Title).Render()),
            (StylesToken, ComposeStyles(document)),
            (CoverToken, ComposeCover(document)),
            (TocToken, ComposeToc(document)),
            (BodyToken, ComposeBody(document)));
    }

    /// <summary>
    /// Fills every placeholder in <paramref name="template"/> in ONE pass over the template.
    ///
    /// <para>Chained <c>string.Replace</c> calls would re-scan text that an earlier call already
    /// substituted, so a document whose title or brand header happened to contain a placeholder
    /// token would have the next value spliced into it — a document titled with the body token
    /// would get the whole document body inside its <c>&lt;title&gt;</c>. Scanning the template
    /// and appending values, rather than searching the accumulating result, makes substituted
    /// content inert by construction. An unrecognised token is left exactly as written.</para>
    /// </summary>
    private static string Fill(string template, params (string Token, string Value)[] values)
    {
        var sb = new System.Text.StringBuilder(template.Length);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var open = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            var close = open < 0 ? -1 : template.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0)
            {
                sb.Append(template, cursor, template.Length - cursor);
                break;
            }

            sb.Append(template, cursor, open - cursor);
            var token = template[open..(close + 2)];
            var match = Array.FindIndex(values, v => string.Equals(v.Token, token, StringComparison.Ordinal));
            sb.Append(match >= 0 ? values[match].Value : token);
            cursor = close + 2;
        }
        return sb.ToString();
    }

    // ── Stylesheet ──────────────────────────────────────────────────────────────────────

    private static string ComposeStyles(Document document)
    {
        var branding = document.Branding;
        var accent = CssColor(branding.AccentColor, BrandingOptions.Default.AccentColor);

        var headerText = document.Options.HeaderOverride ?? branding.HeaderText;
        var logo = branding.Logo;

        // Same predicate the document model used: no text AND no logo means no header band at
        // all. `content: none` does not generate the box, so the rule vanishes with it.
        var hasHeader = !string.IsNullOrEmpty(headerText) || logo is not null;

        return Fill(DocumentPrintTemplates.Styles,
            (PageSizeToken, document.Options.Landscape ? "A4 landscape" : "A4"),
            (PrimaryToken, CssColor(branding.PrimaryColor, BrandingOptions.Default.PrimaryColor)),
            (AccentToken, accent),
            (FontFamilyToken, CssString(
                string.IsNullOrWhiteSpace(branding.FontFamily)
                    ? BrandingOptions.Default.FontFamily
                    : branding.FontFamily)),
            (HeaderTextToken, hasHeader ? CssString(headerText ?? string.Empty) : "none"),
            (HeaderLogoToken, hasHeader && logo is not null ? CssUrl(DataUri(logo)) : "none"),
            (HeaderRuleToken, hasHeader ? $"0.5pt solid {accent}" : "none"),
            // The footer band is unconditional — the page number belongs on every page even when
            // the brand supplies no footer text, exactly as the document model rendered it.
            (FooterTextToken, CssString(document.Options.FooterOverride ?? branding.FooterText ?? string.Empty)));
    }

    // ── Cover ───────────────────────────────────────────────────────────────────────────

    private static string ComposeCover(Document document)
    {
        var branding = document.Branding;

        // Identical gate to the document model's: a cover with neither a logo nor a brand name
        // would be a page carrying only the title, which is not a cover.
        if (!document.Options.CoverPage || (branding.Logo is null && string.IsNullOrEmpty(branding.Name)))
            return string.Empty;

        var cover = MarkupNode.El("section").With("class", "mw-cover");

        if (branding.Logo is { } logo)
            cover = cover.Add(MarkupNode.El("img")
                .With("class", "mw-cover-logo")
                .With("src", DataUri(logo))
                .With("alt", branding.Name));

        cover = cover.Add(MarkupNode.El("div")
            .With("class", "mw-cover-title")
            .Add(MarkupNode.Text(document.Title)));

        if (!string.IsNullOrEmpty(branding.Tagline))
            cover = cover.Add(MarkupNode.El("div")
                .With("class", "mw-cover-tagline")
                .Add(MarkupNode.Text(branding.Tagline)));

        cover = cover
            .Add(MarkupNode.El("hr").With("class", "mw-cover-rule"))
            .Add(MarkupNode.El("div").With("class", "mw-cover-name").Add(MarkupNode.Text(branding.Name)));

        if (!string.IsNullOrEmpty(branding.Website))
            cover = cover.Add(MarkupNode.El("div")
                .With("class", "mw-cover-website")
                .Add(MarkupNode.Text(branding.Website)));

        return cover.Add(MarkupNode.El("div")
                .With("class", "mw-cover-date")
                .Add(MarkupNode.Text(
                    (document.Date ?? DateTime.Today).ToString("d MMMM yyyy", CultureInfo.InvariantCulture))))
            .Render();
    }

    // ── Table of contents ───────────────────────────────────────────────────────────────

    private static string ComposeToc(Document document)
    {
        if (!document.Options.TableOfContents || document.TocHeadings.Length == 0)
            return string.Empty;

        var toc = MarkupNode.El("section")
            .With("class", "mw-toc")
            .Add(MarkupNode.El("div").With("class", "mw-toc-title").Add(MarkupNode.Text(ContentsHeading)));

        // Each entry is a real internal link, which Chromium turns into a PDF link annotation —
        // so the contents list navigates. It carries no page NUMBER: Chromium implements neither
        // `target-counter` nor any other way to read a target's page from CSS, and the two-pass
        // alternative (print, read back where each anchor landed, print again) can silently
        // publish wrong numbers when the inserted digits reflow the list. See the doc page.
        foreach (var heading in document.TocHeadings)
        {
            var level = Math.Clamp(heading.Level, 1, 6);
            toc = toc.Add(MarkupNode.El("p")
                .With("class", $"mw-toc-entry mw-toc-{level.ToString(CultureInfo.InvariantCulture)}")
                .Add(MarkupNode.El("a")
                    .With("href", "#" + heading.AnchorId)
                    .Add(MarkupNode.Text(PlainText(heading.Content)))));
        }

        return toc.Render();
    }

    // ── Body ────────────────────────────────────────────────────────────────────────────

    private static string ComposeBody(Document document) =>
        MarkupNode.Fragment(document.Elements.Select(RenderBlock)).Render();

    private static MarkupNode RenderBlock(DocumentElement element) => element switch
    {
        HeadingElement h => MarkupNode
            .El("h" + Math.Clamp(h.Level, 1, 6).ToString(CultureInfo.InvariantCulture))
            // The anchor is what the contents list links to; it must survive into the PDF.
            .With("id", h.AnchorId)
            .Add(RenderInlines(h.Content)),

        ParagraphElement p => MarkupNode.El("p").Add(RenderInlines(p.Content)),

        CodeBlockElement code => MarkupNode.El("pre")
            .Add(MarkupNode.El("code")
                .With("class", string.IsNullOrEmpty(code.Language) ? null : "language-" + code.Language)
                .Add(MarkupNode.Text(code.Source))),

        // The document model printed the alt text rather than the picture, and so does this: the
        // print document's CSP admits only already-inlined data: URIs, so a linked image would
        // print as a silent blank — and fetching it would be the server, not the reader, reaching
        // out. Naming it keeps the omission visible.
        BlockImageElement img => MarkupNode.El("p")
            .With("class", "mw-image-fallback")
            .Add(MarkupNode.Text($"[image: {img.Alt ?? img.Src}]")),

        MermaidElement { RenderedSvg: { Length: > 0 } svg } => MarkupNode.El("figure")
            .With("class", "mw-mermaid")
            .Add(MarkupNode.Raw(svg)),

        MermaidElement m => MarkupNode.El("div")
            .With("class", "mw-mermaid-fallback")
            .Add(MarkupNode.El("p")
                .With("class", "mw-fallback-note")
                .Add(MarkupNode.Text("Mermaid diagram (not rendered server-side)")))
            .Add(MarkupNode.El("pre").Add(MarkupNode.Text(m.Source))),

        MathElement { RenderedSvg: { Length: > 0 } svg } => MarkupNode.El("figure")
            .With("class", "mw-math")
            .Add(MarkupNode.Raw(svg)),

        MathElement math => MarkupNode.El("div")
            .With("class", "mw-math-fallback")
            .Add(MarkupNode.Text(math.Source)),

        TableElement t => RenderTable(t),

        ListElement list => RenderList(list),

        BlockQuoteElement q => MarkupNode.El("blockquote").Add(q.Content.Select(RenderBlock)),

        HorizontalRuleElement => MarkupNode.El("hr"),

        PageBreakElement => MarkupNode.El("div").With("class", "mw-page-break"),

        ChapterBreakElement chapter => MarkupNode.El("div")
            .With("class", "mw-chapter")
            .Add(MarkupNode.Text(chapter.Title)),

        AnnotationElement ann => MarkupNode.El("aside")
            .With("class", "mw-annotation")
            .Add(MarkupNode.El("p")
                .With("class", "mw-annotation-label")
                .Add(MarkupNode.Text(ann.Kind + (ann.Author is null ? "" : $" — {ann.Author}"))))
            .Add(MarkupNode.El("p")
                .With("class", "mw-annotation-text")
                .Add(MarkupNode.Text(ann.CommentText))),

        _ => MarkupNode.Empty
    };

    private static MarkupNode RenderTable(TableElement table)
    {
        if (table.Rows.Length == 0)
            return MarkupNode.Empty;

        var rows = table.Rows;
        var element = MarkupNode.El("table");

        if (table.HasHeaderRow)
        {
            element = element.Add(MarkupNode.El("thead").Add(
                MarkupNode.El("tr").Add(rows[0].Select(cell =>
                    (MarkupNode)MarkupNode.El("th").Add(RenderInlines(cell))))));
            rows = rows.RemoveAt(0);
        }

        return element.Add(MarkupNode.El("tbody").Add(rows.Select(row =>
            (MarkupNode)MarkupNode.El("tr").Add(row.Select(cell =>
                (MarkupNode)MarkupNode.El("td").Add(RenderInlines(cell)))))));
    }

    private static MarkupNode RenderList(ListElement list) =>
        MarkupNode.El(list.Ordered ? "ol" : "ul")
            .Add(list.Items.Select(item =>
                (MarkupNode)MarkupNode.El("li").Add(item.Content.Select(RenderBlock))));

    private static IEnumerable<MarkupNode> RenderInlines(ImmutableArray<InlineElement> inlines) =>
        inlines.Select(RenderInline);

    private static MarkupNode RenderInline(InlineElement inline)
    {
        switch (inline)
        {
            case TextInline t:
            {
                // Wrap innermost-first so bold+italic+strike compose, exactly as the document
                // model's span flags did.
                MarkupNode node = MarkupNode.Text(t.Text);
                if (t.Code) node = MarkupNode.El("code").Add(node);
                if (t.Strike) node = MarkupNode.El("s").Add(node);
                if (t.Italic) node = MarkupNode.El("em").Add(node);
                if (t.Bold) node = MarkupNode.El("strong").Add(node);
                return node;
            }
            case LineBreakInline:
                return MarkupNode.El("br");
            case LinkInline link:
                return MarkupNode.El("a")
                    .With("href", link.Url)
                    .With("title", link.Title)
                    .Add(RenderInlines(link.Content));
            case ImageInline img:
                // Parity with the document model: inline images become their alt text.
                return string.IsNullOrEmpty(img.Alt)
                    ? MarkupNode.Empty
                    : MarkupNode.El("em").Add(MarkupNode.Text($"[{img.Alt}]"));
            default:
                return MarkupNode.Empty;
        }
    }

    private static string PlainText(ImmutableArray<InlineElement> inlines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is TextInline t) sb.Append(t.Text);
            else if (inline is LinkInline l) sb.Append(PlainText(l.Content));
        }
        return sb.ToString();
    }

    // ── Escaping into CSS ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders <paramref name="value"/> as a CSS string literal, quotes included.
    ///
    /// <para>HTML encoding does not apply inside a <c>&lt;style&gt;</c> element — it is a raw-text
    /// element, so entities are never decoded there and the ONLY things that matter are the CSS
    /// string terminators and the literal sequence that ends the element. All three are handled:
    /// a backslash and a quote are escaped as CSS escapes, and <c>&lt;</c> becomes <c>\3c</c> so a
    /// brand whose header text contains <c>&lt;/style&gt;</c> cannot close the element and start
    /// writing markup. Control characters (a newline would terminate a CSS string) go the same
    /// way.</para>
    /// </summary>
    public static string CssString(string? value)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in value ?? string.Empty)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                // Ends the <style> raw-text element when followed by "/style"; never let it through.
                case '<': sb.Append("\\3c "); break;
                default:
                    if (char.IsControl(c))
                        sb.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Renders a data URI as a CSS <c>url()</c> value with the payload string-escaped.</summary>
    internal static string CssUrl(string dataUri) => $"url({CssString(dataUri)})";

    /// <summary>
    /// Emits a colour only when it IS one, otherwise the brand default.
    ///
    /// <para>A colour is a CSS <i>value</i>, not a string, so it cannot be quoted and escaped —
    /// which makes it the one interpolation that has to be validated instead. Hex is the only form
    /// accepted, and it is the only form that ever worked: the document model passed these
    /// straight to a colour parser that rejected anything else, so a brand carrying a malformed
    /// colour was already broken rather than newly downgraded here.</para>
    /// </summary>
    public static string CssColor(string? value, string fallback) =>
        value is not null && HexColorRegex().IsMatch(value) ? value : fallback;

    private static string DataUri(LogoImage logo) =>
        $"data:{logo.MimeType};base64,{Convert.ToBase64String(logo.Bytes)}";

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorRegex();
}
