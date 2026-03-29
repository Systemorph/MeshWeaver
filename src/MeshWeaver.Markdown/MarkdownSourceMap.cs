using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeshWeaver.Markdown;

/// <summary>
/// Builds a position map from rendered plain text back to markdown source positions.
/// Uses Markdig's built-in ToPlainText for the rendered text (guaranteed to match browser),
/// then walks the AST source spans to build the character-level offset map.
/// </summary>
public static class MarkdownSourceMap
{
    /// <summary>
    /// Builds a map where map[renderedTextIndex] = sourceIndex.
    /// Also returns the rendered plain text for matching against the browser selection.
    /// The input should be clean markdown (annotation markers already stripped).
    /// </summary>
    public static (string PlainText, int[] Map) BuildRenderedToSourceMap(string cleanMarkdown)
    {
        if (string.IsNullOrEmpty(cleanMarkdown))
            return ("", []);

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        var doc = Markdig.Markdown.Parse(cleanMarkdown, pipeline);

        // Use Markdig's own plain text rendering — guaranteed same text as browser
        var plainText = Markdig.Markdown.ToPlainText(cleanMarkdown, pipeline);

        // Build position map by walking AST and collecting source positions
        var mapBuilder = new List<int>();
        CollectSourcePositions(doc, mapBuilder, cleanMarkdown);

        // Pad map to match plainText length (whitespace/newlines from renderer)
        while (mapBuilder.Count < plainText.Length)
            mapBuilder.Add(mapBuilder.Count > 0 ? mapBuilder[^1] + 1 : 0);

        return (plainText, mapBuilder.Take(plainText.Length).ToArray());
    }

    private static void CollectSourcePositions(ContainerBlock container, List<int> map, string source)
    {
        foreach (var block in container)
        {
            if (block is LeafBlock leaf && leaf.Inline != null)
            {
                CollectInlinePositions(leaf.Inline, map);

                // Newline after block
                if (map.Count > 0)
                    map.Add(Math.Min(map[^1] + 1, source.Length));
            }
            else if (block is ContainerBlock nested)
            {
                CollectSourcePositions(nested, map, source);
            }
        }
    }

    private static void CollectInlinePositions(ContainerInline container, List<int> map)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                // LiteralInline — actual text content
                case LiteralInline literal:
                {
                    var content = literal.Content;
                    var srcStart = literal.Span.Start;
                    for (int i = 0; i < content.Length; i++)
                        map.Add(srcStart + i);
                    break;
                }

                case CodeInline code:
                {
                    var srcStart = inline.Span.Start;
                    for (int i = 0; i < code.Content.Length; i++)
                        map.Add(srcStart + i + 1); // +1 for opening backtick
                    break;
                }

                case LineBreakInline:
                    map.Add(inline.Span.Start);
                    break;

                // ContainerInline (EmphasisInline, LinkInline) — recurse
                case ContainerInline nested:
                    CollectInlinePositions(nested, map);
                    break;
            }
        }
    }
}
