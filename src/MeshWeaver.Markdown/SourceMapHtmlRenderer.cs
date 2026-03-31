using System.IO;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeshWeaver.Markdown;

/// <summary>
/// Custom HtmlRenderer that injects data-start/data-end attributes on block and inline elements.
/// These source positions (from Markdig's AST Span) allow JavaScript to map
/// a browser text selection back to the exact markdown source range.
/// </summary>
public class SourceMapHtmlRenderer : HtmlRenderer
{
    public SourceMapHtmlRenderer(TextWriter writer) : base(writer)
    {
        // Replace default renderers with source-map-aware versions
        ObjectRenderers.Replace<ParagraphRenderer>(new SourceMapParagraphRenderer());
        ObjectRenderers.Replace<HeadingRenderer>(new SourceMapHeadingRenderer());
        ObjectRenderers.Replace<ListRenderer>(new SourceMapListRenderer());
        ObjectRenderers.Replace<QuoteBlockRenderer>(new SourceMapQuoteBlockRenderer());
        ObjectRenderers.Replace<EmphasisInlineRenderer>(new SourceMapEmphasisRenderer());
        ObjectRenderers.Replace<LinkInlineRenderer>(new SourceMapLinkRenderer());
        ObjectRenderers.Replace<CodeInlineRenderer>(new SourceMapCodeInlineRenderer());
    }

    /// <summary>
    /// Renders a MarkdownDocument to HTML with source position annotations.
    /// </summary>
    public static string RenderWithSourceMap(string markdown, MarkdownPipeline pipeline)
    {
        var doc = Markdig.Markdown.Parse(markdown, pipeline);
        using var sw = new StringWriter();
        var renderer = new SourceMapHtmlRenderer(sw);
        pipeline.Setup(renderer);
        renderer.Render(doc);
        sw.Flush();
        return sw.ToString();
    }
}

// Block renderers with data-start/data-end

internal class SourceMapParagraphRenderer : HtmlObjectRenderer<ParagraphBlock>
{
    protected override void Write(HtmlRenderer renderer, ParagraphBlock obj)
    {
        if (!renderer.ImplicitParagraph)
        {
            renderer.Write("<p");
            renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
            renderer.WriteAttributes(obj);
            renderer.Write('>');
        }
        renderer.WriteLeafInline(obj);
        if (!renderer.ImplicitParagraph)
            renderer.WriteLine("</p>");
    }
}

internal class SourceMapHeadingRenderer : HtmlObjectRenderer<HeadingBlock>
{
    protected override void Write(HtmlRenderer renderer, HeadingBlock obj)
    {
        var level = obj.Level;
        renderer.Write($"<h{level}");
        renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
        renderer.WriteAttributes(obj);
        renderer.Write('>');
        renderer.WriteLeafInline(obj);
        renderer.WriteLine($"</h{level}>");
    }
}

internal class SourceMapListRenderer : HtmlObjectRenderer<ListBlock>
{
    protected override void Write(HtmlRenderer renderer, ListBlock obj)
    {
        var tag = obj.IsOrdered ? "ol" : "ul";
        renderer.Write($"<{tag}");
        renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
        renderer.WriteAttributes(obj);
        renderer.WriteLine('>');

        foreach (var item in obj)
        {
            if (item is ListItemBlock li)
            {
                renderer.Write("<li");
                renderer.Write($" data-start=\"{li.Span.Start}\" data-end=\"{li.Span.End}\"");
                renderer.WriteAttributes(li);
                renderer.Write('>');
                renderer.WriteChildren(li);
                renderer.WriteLine("</li>");
            }
        }

        renderer.WriteLine($"</{tag}>");
    }
}

internal class SourceMapQuoteBlockRenderer : HtmlObjectRenderer<QuoteBlock>
{
    protected override void Write(HtmlRenderer renderer, QuoteBlock obj)
    {
        renderer.Write("<blockquote");
        renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
        renderer.WriteAttributes(obj);
        renderer.WriteLine('>');
        renderer.WriteChildren(obj);
        renderer.WriteLine("</blockquote>");
    }
}

// Inline renderers with data-start/data-end

internal class SourceMapEmphasisRenderer : HtmlObjectRenderer<EmphasisInline>
{
    protected override void Write(HtmlRenderer renderer, EmphasisInline obj)
    {
        var tag = obj.DelimiterCount == 2 ? "strong" : "em";
        renderer.Write($"<{tag}");
        renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
        renderer.Write('>');
        renderer.WriteChildren(obj);
        renderer.Write($"</{tag}>");
    }
}

internal class SourceMapLinkRenderer : HtmlObjectRenderer<LinkInline>
{
    protected override void Write(HtmlRenderer renderer, LinkInline obj)
    {
        if (obj.IsImage)
        {
            renderer.Write("<img");
            renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
            renderer.Write($" src=\"{obj.Url}\"");
            renderer.Write($" alt=\"");
            renderer.WriteChildren(obj);
            renderer.Write("\" />");
        }
        else
        {
            renderer.Write("<a");
            renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
            renderer.Write($" href=\"{obj.Url}\"");
            if (!string.IsNullOrEmpty(obj.Title))
                renderer.Write($" title=\"{obj.Title}\"");
            renderer.Write('>');
            renderer.WriteChildren(obj);
            renderer.Write("</a>");
        }
    }
}

internal class SourceMapCodeInlineRenderer : HtmlObjectRenderer<CodeInline>
{
    protected override void Write(HtmlRenderer renderer, CodeInline obj)
    {
        renderer.Write("<code");
        renderer.Write($" data-start=\"{obj.Span.Start}\" data-end=\"{obj.Span.End}\"");
        renderer.Write('>');
        renderer.WriteEscape(obj.Content);
        renderer.Write("</code>");
    }
}
