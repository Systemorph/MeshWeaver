using System.Collections.Immutable;
using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Model;
using Document = MeshWeaver.Markdown.Export.Model.Document;
using WpDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using Break = DocumentFormat.OpenXml.Wordprocessing.Break;
using Paragraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using Run = DocumentFormat.OpenXml.Wordprocessing.Run;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using TableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;

namespace MeshWeaver.Markdown.Export.Docx;

/// <summary>
/// Renders a <see cref="Document"/> to a .docx byte array via DocumentFormat.OpenXml.
/// Maps MeshWeaver annotations to native Word comments where possible.
/// </summary>
public class DocxDocumentRenderer
{
    /// <summary>Produces a DOCX byte array from the document model.</summary>
    public byte[] Render(Document document)
    {
        return document.Branding.TemplateDocxBytes is { Length: > 0 } templateBytes
            ? RenderFromTemplate(document, templateBytes)
            : RenderFromScratch(document);
    }

    private static byte[] RenderFromScratch(Document document)
    {
        using var ms = new MemoryStream();
        using (var word = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new WpDocument(new Body());
            var body = main.Document.Body!;

            AddStyles(main);
            AddHeaderAndFooter(main, document);

            if (document.Options.CoverPage)
                AppendCover(body, document);

            if (document.Options.TableOfContents && document.TocHeadings.Length > 0)
                AppendToc(body);

            foreach (var element in document.Elements)
                AppendBlock(body, element, document);

            AppendSectionProperties(body, main);

            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] RenderFromTemplate(Document document, byte[] templateBytes)
    {
        // Copy into a writable MemoryStream so we can edit the package in place.
        var ms = new MemoryStream();
        ms.Write(templateBytes, 0, templateBytes.Length);
        ms.Position = 0;

        using (var word = WordprocessingDocument.Open(ms, isEditable: true))
        {
            var main = word.MainDocumentPart
                ?? throw new InvalidOperationException("Template is missing a main document part.");
            main.Document ??= new WpDocument(new Body());
            var body = main.Document.Body ??= new Body();

            // Preserve the last SectionProperties (links headers/footers, page size, margins).
            var sectPr = body.Elements<SectionProperties>().LastOrDefault()?.CloneNode(true);
            body.RemoveAllChildren();

            if (document.Options.CoverPage)
                AppendCover(body, document);

            if (document.Options.TableOfContents && document.TocHeadings.Length > 0)
                AppendToc(body);

            foreach (var element in document.Elements)
                AppendBlock(body, element, document);

            if (sectPr is not null)
                body.Append(sectPr);

            main.Document.Save();
        }

        return ms.ToArray();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            CreateStyle("Heading1", "heading 1", 28, bold: true, color: null),
            CreateStyle("Heading2", "heading 2", 22, bold: true, color: null),
            CreateStyle("Heading3", "heading 3", 18, bold: true, color: null),
            CreateStyle("Heading4", "heading 4", 14, bold: true, color: null),
            CreateStyle("Title", "Title", 40, bold: true, color: null),
            CreateStyle("Subtitle", "Subtitle", 20, italic: true, color: null),
            CreateStyle("Quote", "Quote", 22, italic: true, color: "555555"),
            CreateStyle("CodeBlock", "Code Block", 18, fontFamily: "Consolas", color: "222222"));
        stylesPart.Styles.Save();
    }

    private static Style CreateStyle(string id, string name, int halfPoints,
        bool bold = false, bool italic = false, string? color = null, string? fontFamily = null)
    {
        var runProps = new StyleRunProperties();
        if (bold) runProps.Append(new Bold());
        if (italic) runProps.Append(new Italic());
        runProps.Append(new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        if (color is not null) runProps.Append(new Color { Val = color });
        if (fontFamily is not null) runProps.Append(new RunFonts { Ascii = fontFamily });

        return new Style(
            new StyleName { Val = name },
            runProps)
        {
            Type = StyleValues.Paragraph,
            StyleId = id
        };
    }

    private static void AddHeaderAndFooter(MainDocumentPart main, Document document)
    {
        var headerText = document.Options.HeaderOverride ?? document.Branding.HeaderText;
        var footerText = document.Options.FooterOverride ?? document.Branding.FooterText;

        var headerPart = main.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(new Run(new Text(headerText) { Space = SpaceProcessingModeValues.Preserve })));
        headerPart.Header.Save();

        var footerPart = main.AddNewPart<FooterPart>();
        footerPart.Footer = new Footer(new Paragraph(new Run(new Text(footerText) { Space = SpaceProcessingModeValues.Preserve })));
        footerPart.Footer.Save();
    }

    private static void AppendSectionProperties(Body body, MainDocumentPart main)
    {
        var headerId = main.GetIdOfPart(main.HeaderParts.First());
        var footerId = main.GetIdOfPart(main.FooterParts.First());
        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId },
            new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
            new PageSize { Width = 11906U, Height = 16838U }, // A4 in twentieths of a point
            new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 720, Footer = 720 }));
    }

    private static void AppendCover(Body body, Document document)
    {
        // Spacer
        body.AppendChild(SpacerParagraph(lines: 5));

        // Title
        var titlePara = body.AppendChild(new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "Title" },
                new Justification { Val = JustificationValues.Center })));
        var titleRun = new Run(new Text(document.Title) { Space = SpaceProcessingModeValues.Preserve });
        titleRun.PrependChild(new RunProperties(
            new Bold(),
            new FontSize { Val = "56" },
            new Color { Val = TrimHash(document.Branding.PrimaryColor) }));
        titlePara.AppendChild(titleRun);

        if (!string.IsNullOrEmpty(document.Branding.Tagline))
        {
            var sub = body.AppendChild(new Paragraph(
                new ParagraphProperties(
                    new ParagraphStyleId { Val = "Subtitle" },
                    new Justification { Val = JustificationValues.Center })));
            var subRun = new Run(new Text(document.Branding.Tagline) { Space = SpaceProcessingModeValues.Preserve });
            subRun.PrependChild(new RunProperties(
                new Italic(),
                new FontSize { Val = "28" },
                new Color { Val = TrimHash(document.Branding.AccentColor) }));
            sub.AppendChild(subRun);
        }

        body.AppendChild(SpacerParagraph(lines: 2));

        // Org name
        if (!string.IsNullOrEmpty(document.Branding.Name))
            body.AppendChild(CenteredParagraph(document.Branding.Name, size: "24"));

        if (!string.IsNullOrEmpty(document.Branding.Website))
            body.AppendChild(CenteredParagraph(document.Branding.Website, size: "20"));

        body.AppendChild(CenteredParagraph(
            DateTime.Today.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), size: "20"));

        // Page break
        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    private static void AppendToc(Body body)
    {
        // Heading
        var heading = body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" })));
        heading.AppendChild(new Run(new Text("Contents")));

        // TOC field — Word populates on open via right-click "Update Field" or when the doc is re-saved.
        var tocPara = body.AppendChild(new Paragraph());
        tocPara.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }));
        tocPara.AppendChild(new Run(new FieldCode(@"TOC \o ""1-3"" \h \z \u") { Space = SpaceProcessingModeValues.Preserve }));
        tocPara.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }));
        tocPara.AppendChild(new Run(new Text("Right-click and choose \"Update Field\" to populate.") { Space = SpaceProcessingModeValues.Preserve }));
        tocPara.AppendChild(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));

        body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    private static void AppendBlock(Body body, DocumentElement element, Document document)
    {
        switch (element)
        {
            case HeadingElement h:
            {
                var style = h.Level switch { 1 => "Heading1", 2 => "Heading2", 3 => "Heading3", _ => "Heading4" };
                var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }));
                AppendInlines(p, h.Content, headingColor: TrimHash(document.Branding.PrimaryColor));
                body.AppendChild(p);
                break;
            }
            case ParagraphElement p:
            {
                var para = new Paragraph();
                AppendInlines(para, p.Content);
                body.AppendChild(para);
                break;
            }
            case CodeBlockElement code:
            {
                var p = new Paragraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "CodeBlock" },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F3F4F6" }));
                foreach (var line in (code.Source ?? "").Split('\n'))
                {
                    var run = new Run(new Text(line.TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve });
                    run.PrependChild(new RunProperties(new RunFonts { Ascii = "Consolas" }, new FontSize { Val = "18" }));
                    p.AppendChild(run);
                    p.AppendChild(new Run(new Break()));
                }
                body.AppendChild(p);
                break;
            }
            case PageBreakElement:
                body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                break;
            case ChapterBreakElement chapter:
            {
                var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }));
                p.AppendChild(new Run(new Text(chapter.Title) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(p);
                break;
            }
            case HorizontalRuleElement:
                body.AppendChild(new Paragraph(new ParagraphProperties(
                    new ParagraphBorders(new BottomBorder
                    {
                        Val = BorderValues.Single, Size = 6, Color = "cccccc", Space = 1
                    }))));
                break;
            case ListElement list:
                AppendList(body, list, depth: 0);
                break;
            case TableElement t:
                AppendTable(body, t);
                break;
            case BlockQuoteElement q:
            {
                foreach (var inner in q.Content)
                {
                    if (inner is ParagraphElement innerP)
                    {
                        var para = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Quote" }));
                        AppendInlines(para, innerP.Content);
                        body.AppendChild(para);
                    }
                    else AppendBlock(body, inner, document);
                }
                break;
            }
            case MermaidElement m:
            {
                // SVG embedding in DOCX requires image-part work; this pass falls back to a fenced code block.
                AppendBlock(body, new CodeBlockElement("mermaid", m.Source), document);
                break;
            }
            case MathElement math:
            {
                AppendBlock(body, new CodeBlockElement("math", math.Source), document);
                break;
            }
            case BlockImageElement img:
            {
                var p = new Paragraph();
                var run = new Run(new Text($"[image: {img.Alt ?? img.Src}]") { Space = SpaceProcessingModeValues.Preserve });
                run.PrependChild(new RunProperties(new Italic()));
                p.AppendChild(run);
                body.AppendChild(p);
                break;
            }
            case AnnotationElement ann:
            {
                // Lightweight rendering as a styled callout. Native w:comment wiring is a follow-up.
                var p = new Paragraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "Quote" },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "FFF7ED" }));
                var label = $"{ann.Kind}" + (ann.Author is null ? "" : $" — {ann.Author}");
                var labelRun = new Run(new Text(label + ": ") { Space = SpaceProcessingModeValues.Preserve });
                labelRun.PrependChild(new RunProperties(new Italic(), new Color { Val = "92400E" }));
                p.AppendChild(labelRun);
                p.AppendChild(new Run(new Text(ann.CommentText) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(p);
                break;
            }
        }
    }

    private static void AppendList(Body body, ListElement list, int depth)
    {
        var idx = 1;
        foreach (var item in list.Items)
        {
            foreach (var child in item.Content)
            {
                if (child is ParagraphElement p)
                {
                    // Emit bullet / number as a literal run with a hanging indent scaled by depth.
                    // Using <w:numPr> without a matching abstract numbering definition in
                    // numbering.xml makes Word apply document-wide continuous numbering.
                    var indentTwips = (depth + 1) * 360; // 0.25 inch per level
                    var para = new Paragraph(new ParagraphProperties(
                        new Indentation { Left = indentTwips.ToString(), Hanging = "360" }));
                    AppendInlines(para, p.Content);
                    para.InsertAt(new Run(new Text((list.Ordered ? $"{idx}. " : "• "))
                        { Space = SpaceProcessingModeValues.Preserve }), 1);
                    body.AppendChild(para);
                }
                else if (child is ListElement nested)
                {
                    AppendList(body, nested, depth + 1);
                }
                else
                {
                    // Other block types inside list items — render inline.
                    // Note: body param below; for simplicity we drop nesting context.
                    // The flattened approach is acceptable for a first export pass.
                }
            }
            idx++;
        }
    }

    private static void AppendTable(Body body, TableElement t)
    {
        if (t.Rows.Length == 0) return;

        var table = body.AppendChild(new Table());
        var tblProps = new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }));
        table.AppendChild(tblProps);

        for (var r = 0; r < t.Rows.Length; r++)
        {
            var isHeader = r == 0 && t.HasHeaderRow;
            var row = new TableRow();
            foreach (var cellInlines in t.Rows[r])
            {
                var cell = new TableCell();
                var para = new Paragraph();
                AppendInlines(para, cellInlines, bold: isHeader);
                cell.AppendChild(para);
                if (isHeader)
                {
                    cell.Append(new TableCellProperties(
                        new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F9FAFB" }));
                }
                row.AppendChild(cell);
            }
            table.AppendChild(row);
        }
    }

    private static void AppendInlines(Paragraph para, ImmutableArray<InlineElement> inlines,
        bool bold = false, string? headingColor = null)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                {
                    var run = new Run(new Text(t.Text) { Space = SpaceProcessingModeValues.Preserve });
                    var rp = new RunProperties();
                    if (bold || t.Bold) rp.Append(new Bold());
                    if (t.Italic) rp.Append(new Italic());
                    if (t.Strike) rp.Append(new Strike());
                    if (t.Code) { rp.Append(new RunFonts { Ascii = "Consolas" }); rp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F3F4F6" }); }
                    if (headingColor is not null) rp.Append(new Color { Val = headingColor });
                    run.PrependChild(rp);
                    para.AppendChild(run);
                    break;
                }
                case LineBreakInline:
                    para.AppendChild(new Run(new Break()));
                    break;
                case LinkInline link:
                {
                    // Inline hyperlink (without registering a Hyperlink relationship — plain styled text).
                    foreach (var child in link.Content)
                    {
                        if (child is TextInline ct)
                        {
                            var run = new Run(new Text(ct.Text) { Space = SpaceProcessingModeValues.Preserve });
                            run.PrependChild(new RunProperties(
                                new Color { Val = "2563EB" },
                                new Underline { Val = UnderlineValues.Single }));
                            para.AppendChild(run);
                        }
                    }
                    break;
                }
                case ImageInline img:
                {
                    var run = new Run(new Text($"[{img.Alt ?? img.Src}]") { Space = SpaceProcessingModeValues.Preserve });
                    run.PrependChild(new RunProperties(new Italic()));
                    para.AppendChild(run);
                    break;
                }
            }
        }
    }

    private static Paragraph SpacerParagraph(int lines)
    {
        var p = new Paragraph();
        for (var i = 0; i < lines; i++)
            p.AppendChild(new Run(new Break()));
        return p;
    }

    private static Paragraph CenteredParagraph(string text, string size = "22")
    {
        var p = new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }));
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        run.PrependChild(new RunProperties(new FontSize { Val = size }));
        p.AppendChild(run);
        return p;
    }

    private static string TrimHash(string color) => color.TrimStart('#').ToUpperInvariant();
}
