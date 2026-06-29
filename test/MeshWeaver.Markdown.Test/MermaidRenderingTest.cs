using Xunit;

namespace MeshWeaver.Markdown.Test;

/// <summary>
/// A ```mermaid fenced block renders into a <c>&lt;div class='mermaid'&gt;</c> whose
/// diagram body is HTML-ESCAPED. Mermaid class diagrams contain <c>&lt;</c> — the
/// stereotype markers <c>&lt;&lt;enumeration&gt;&gt;</c>/<c>&lt;&lt;interface&gt;&gt;</c> and the
/// inheritance arrow <c>&lt;|--</c>. Written raw, the browser / HtmlAgilityPack parse those
/// as stray tags and the diagram text is destroyed before Mermaid reads it (the
/// "stereotype diagrams don't render" bug). Escaped, the entities round-trip back to the
/// literal source via <c>textContent</c> / <c>DeEntitize(InnerText)</c>.
/// </summary>
public class MermaidRenderingTest
{
    private static string RenderHtml(string markdown) => MarkdownViewLogic.Render(markdown, null, null).Html;

    [Fact]
    public void MermaidFence_RendersIntoMermaidDiv()
    {
        var html = RenderHtml("```mermaid\nclassDiagram\n    class Foo\n```");
        html.Should().Contain("<div class='mermaid'>");
        html.Should().Contain("classDiagram");
    }

    [Fact]
    public void Stereotype_IsHtmlEscaped_SoBrowserDoesNotParseItAsATag()
    {
        var md = "```mermaid\nclassDiagram\n    class Color {\n        <<enumeration>>\n        Red\n    }\n```";
        var html = RenderHtml(md);
        html.Should().Contain("&lt;&lt;enumeration&gt;&gt;",
            "the stereotype must be escaped so it round-trips through textContent/InnerText to the literal mermaid source");
        html.Should().NotContain("<enumeration>",
            "a raw <enumeration> would be parsed as an HTML element and the stereotype line lost");
    }

    [Fact]
    public void InheritanceArrow_IsHtmlEscaped()
    {
        var html = RenderHtml("```mermaid\nclassDiagram\n    Base <|-- Derived\n```");
        html.Should().Contain("Base &lt;|-- Derived");
        html.Should().NotContain("Base <|-- Derived");
    }

    [Fact]
    public void PlainArrow_StillRenders()
    {
        var html = RenderHtml("```mermaid\nclassDiagram\n    A --> B\n```");
        html.Should().Contain("<div class='mermaid'>");
        // '>' is escaped uniformly to &gt;; it decodes back to '>' in textContent, so the
        // diagram is unchanged for Mermaid while staying valid HTML.
        html.Should().Contain("A --&gt; B");
    }
}
