using System.Collections.Immutable;
using System.Globalization;
using MeshWeaver.Markdown.Export.Branding;
using MeshWeaver.Markdown.Export.Model;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Document = MeshWeaver.Markdown.Export.Model.Document;
using QpdDocument = QuestPDF.Fluent.Document;

namespace MeshWeaver.Markdown.Export.Pdf;

/// <summary>
/// Renders a <see cref="Document"/> to a PDF byte array using QuestPDF.
/// Ships with QuestPDF's Community License (set once at module init by <c>AddMarkdownExport</c>).
/// </summary>
public class PdfDocumentRenderer
{
    /// <summary>Produces a PDF byte array from the document model.</summary>
    public byte[] Render(Document document)
    {
        return QpdDocument.Create(c =>
        {
            if (document.Options.CoverPage && (document.Branding.Logo is not null || !string.IsNullOrEmpty(document.Branding.Name)))
                c.Page(p => ComposeCover(p, document));

            if (document.Options.TableOfContents && document.TocHeadings.Length > 0)
                c.Page(p => ComposeToc(p, document));

            c.Page(p => ComposeBody(p, document));
        }).GeneratePdf();
    }

    private static void ComposeCover(PageDescriptor page, Document document)
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontFamily(document.Branding.FontFamily));

        page.Content().Column(col =>
        {
            col.Spacing(20);
            col.Item().Height(3, Unit.Centimetre);

            if (document.Branding.Logo is { } logo && !logo.IsSvg)
            {
                col.Item().AlignCenter().MaxHeight(3, Unit.Centimetre).Image(logo.Bytes).FitHeight();
            }
            else if (document.Branding.Logo is { IsSvg: true } svgLogo)
            {
                col.Item().AlignCenter().MaxHeight(3, Unit.Centimetre).Svg(System.Text.Encoding.UTF8.GetString(svgLogo.Bytes));
            }

            col.Item().AlignCenter().Text(document.Title)
                .FontSize(28).Bold().FontColor(document.Branding.PrimaryColor);

            if (!string.IsNullOrEmpty(document.Branding.Tagline))
                col.Item().AlignCenter().Text(document.Branding.Tagline)
                    .FontSize(14).FontColor(document.Branding.AccentColor);

            col.Item().Height(2, Unit.Centimetre);
            col.Item().AlignCenter().LineHorizontal(1).LineColor(document.Branding.AccentColor);
            col.Item().AlignCenter().Text(document.Branding.Name).FontSize(12);

            if (!string.IsNullOrEmpty(document.Branding.Website))
                col.Item().AlignCenter().Text(document.Branding.Website).FontSize(10);

            col.Item().AlignCenter().Text(DateTime.Today.ToString("d MMMM yyyy", CultureInfo.InvariantCulture))
                .FontSize(10);
        });
    }

    private static void ComposeToc(PageDescriptor page, Document document)
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontFamily(document.Branding.FontFamily));
        ApplyHeader(page, document);
        ApplyFooter(page, document);

        page.Content().Column(col =>
        {
            col.Item().Text("Contents").FontSize(20).Bold().FontColor(document.Branding.PrimaryColor);
            col.Item().PaddingBottom(10);

            foreach (var heading in document.TocHeadings)
            {
                var indent = (heading.Level - 1) * 15;
                var title = PlainText(heading.Content);
                var isTop = heading.Level == 1;
                col.Item().PaddingLeft(indent).Row(row =>
                {
                    row.RelativeItem().Text(text =>
                    {
                        var span = text.Hyperlink(title, heading.AnchorId)
                            .FontSize(isTop ? 12 : 11);
                        if (isTop) span.Bold();
                    });
                    row.ConstantItem(40).AlignRight().Text(text =>
                        text.BeginPageNumberOfSection(heading.AnchorId));
                });
            }
        });
    }

    private static void ComposeBody(PageDescriptor page, Document document)
    {
        page.Size(PageSizes.A4);
        page.Margin(2, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontFamily(document.Branding.FontFamily).FontSize(11));
        ApplyHeader(page, document);
        ApplyFooter(page, document);

        page.Content().Column(col =>
        {
            col.Spacing(8);
            foreach (var element in document.Elements)
                RenderBlock(col.Item(), element, document);
        });
    }

    private static void ApplyHeader(PageDescriptor page, Document document)
    {
        var text = document.Options.HeaderOverride ?? document.Branding.HeaderText;
        if (string.IsNullOrEmpty(text)) return;
        page.Header().PaddingBottom(5).BorderBottom(0.5f).BorderColor(document.Branding.AccentColor)
            .Text(text).FontSize(9).FontColor(document.Branding.AccentColor);
    }

    private static void ApplyFooter(PageDescriptor page, Document document)
    {
        var text = document.Options.FooterOverride ?? document.Branding.FooterText;
        page.Footer().Row(row =>
        {
            row.RelativeItem().AlignLeft().Text(text ?? "").FontSize(9).FontColor(document.Branding.AccentColor);
            row.ConstantItem(100).AlignRight().Text(x =>
            {
                x.CurrentPageNumber().FontSize(9);
                x.Span(" / ").FontSize(9);
                x.TotalPages().FontSize(9);
            });
        });
    }

    private static void RenderBlock(IContainer slot, DocumentElement element, Document document)
    {
        switch (element)
        {
            case HeadingElement h:
                slot.Section(h.AnchorId).Text(text =>
                {
                    var size = h.Level switch { 1 => 22f, 2 => 18f, 3 => 14f, 4 => 12f, _ => 11f };
                    foreach (var inline in h.Content)
                        RenderInline(text, inline, size, bold: true, color: document.Branding.PrimaryColor);
                });
                break;

            case ParagraphElement p:
                slot.Text(text =>
                {
                    foreach (var inline in p.Content)
                        RenderInline(text, inline, 11f);
                });
                break;

            case CodeBlockElement code:
                slot.Background("#f3f4f6").Padding(8).Text(code.Source)
                    .FontFamily(Fonts.Consolas).FontSize(9);
                break;

            case BlockImageElement img:
                // Path-based image embedding not supported in this pass (no content service here).
                slot.Text($"[image: {img.Alt ?? img.Src}]").Italic().FontColor(Colors.Grey.Medium);
                break;

            case MermaidElement m when m.RenderedSvg is { Length: > 0 } svg:
                slot.AlignCenter().MaxHeight(10, Unit.Centimetre).Svg(svg);
                break;

            case MermaidElement m:
                slot.Background("#fef3c7").Padding(8).Column(c =>
                {
                    c.Item().Text("Mermaid diagram (not rendered server-side)").Italic().FontSize(9);
                    c.Item().Text(m.Source).FontFamily(Fonts.Consolas).FontSize(9);
                });
                break;

            case MathElement math when math.RenderedSvg is { Length: > 0 } svg:
                slot.AlignCenter().Svg(svg);
                break;

            case MathElement math:
                slot.Background("#eef2ff").Padding(8).Text(math.Source)
                    .FontFamily(Fonts.Consolas).FontSize(10);
                break;

            case TableElement t:
                RenderTable(slot, t, document);
                break;

            case ListElement list:
                RenderList(slot, list, document);
                break;

            case BlockQuoteElement q:
                slot.BorderLeft(3).BorderColor(document.Branding.AccentColor)
                    .PaddingLeft(10).Column(col =>
                    {
                        foreach (var inner in q.Content)
                            RenderBlock(col.Item().PaddingVertical(2), inner, document);
                    });
                break;

            case HorizontalRuleElement:
                slot.PaddingVertical(6).LineHorizontal(0.75f).LineColor(Colors.Grey.Lighten2);
                break;

            case PageBreakElement:
                slot.PageBreak();
                break;

            case ChapterBreakElement chapter:
                slot.Text(chapter.Title).FontSize(20).Bold().FontColor(document.Branding.PrimaryColor);
                break;

            case AnnotationElement ann:
                // Annotations are inlined as a callout with author attribution.
                slot.Background("#fff7ed").BorderLeft(3).BorderColor("#f59e0b")
                    .Padding(8).Column(c =>
                    {
                        var label = $"{ann.Kind}" + (ann.Author is null ? "" : $" — {ann.Author}");
                        c.Item().Text(label).FontSize(9).Italic().FontColor("#92400e");
                        c.Item().Text(ann.CommentText).FontSize(10);
                    });
                break;
        }
    }

    private static void RenderTable(IContainer slot, TableElement t, Document document)
    {
        if (t.Rows.Length == 0) return;
        var colCount = t.Rows[0].Length;
        slot.Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                for (var i = 0; i < colCount; i++) cols.RelativeColumn();
            });

            var rowIndex = 0;
            foreach (var row in t.Rows)
            {
                var isHeader = rowIndex == 0 && t.HasHeaderRow;
                foreach (var cell in row)
                {
                    var c = table.Cell().Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                    if (isHeader) c = c.Background("#f9fafb");
                    c.Text(text =>
                    {
                        foreach (var inline in cell)
                            RenderInline(text, inline, 10f, bold: isHeader);
                    });
                }
                rowIndex++;
            }
        });
    }

    private static void RenderList(IContainer slot, ListElement list, Document document)
    {
        slot.Column(col =>
        {
            var idx = 1;
            foreach (var item in list.Items)
            {
                col.Item().Row(row =>
                {
                    row.ConstantItem(20).AlignRight().Text(list.Ordered ? $"{idx}." : "•").FontSize(11);
                    row.RelativeItem().Column(inner =>
                    {
                        foreach (var child in item.Content)
                            RenderBlock(inner.Item(), child, document);
                    });
                });
                idx++;
            }
        });
    }

    private static void RenderInline(
        TextDescriptor text, InlineElement inline,
        float size, bool bold = false, string? color = null)
    {
        switch (inline)
        {
            case TextInline t:
            {
                var span = text.Span(t.Text);
                span.FontSize(size);
                if (bold || t.Bold) span.Bold();
                if (t.Italic) span.Italic();
                if (t.Strike) span.Strikethrough();
                if (t.Code) span.FontFamily(Fonts.Consolas).BackgroundColor("#f3f4f6");
                if (color is not null) span.FontColor(color);
                break;
            }
            case LineBreakInline:
                text.Line("");
                break;
            case LinkInline link:
                foreach (var child in link.Content)
                {
                    if (child is TextInline tx)
                    {
                        var span = text.Hyperlink(tx.Text, link.Url);
                        span.FontSize(size).Underline();
                        if (bold || tx.Bold) span.Bold();
                        if (tx.Italic) span.Italic();
                    }
                }
                break;
            case ImageInline:
                // Inline images aren't supported in QuestPDF Text — fall back to the alt text.
                if (inline is ImageInline img && !string.IsNullOrEmpty(img.Alt))
                    text.Span($"[{img.Alt}]").FontSize(size).Italic();
                break;
        }
    }

    private static string PlainText(ImmutableArray<InlineElement> inlines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in inlines)
        {
            if (i is TextInline t) sb.Append(t.Text);
            else if (i is LinkInline l) sb.Append(PlainText(l.Content));
        }
        return sb.ToString();
    }
}

