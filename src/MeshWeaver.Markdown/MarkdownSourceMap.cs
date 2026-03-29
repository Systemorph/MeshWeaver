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

    /// <summary>
    /// Finds a text fragment in markdown source using the rendered-to-source map.
    /// Returns the source position (in cleanMarkdown) or -1 if not found.
    /// Searches in the rendered plain text and maps back to source position.
    /// </summary>
    public static int FindFragmentInSource(string cleanMarkdown, string fragment, int searchFromSourcePos = 0)
    {
        if (string.IsNullOrEmpty(fragment) || string.IsNullOrEmpty(cleanMarkdown))
            return -1;

        var (plainText, map) = BuildRenderedToSourceMap(cleanMarkdown);

        // Find the fragment in the rendered plain text
        var plainIdx = plainText.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
        while (plainIdx >= 0)
        {
            var sourcePos = plainIdx < map.Length ? map[plainIdx] : cleanMarkdown.Length;
            if (sourcePos >= searchFromSourcePos)
                return sourcePos;
            // Try next occurrence
            plainIdx = plainText.IndexOf(fragment, plainIdx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    /// <summary>
    /// Finds the end position of a fragment in the source.
    /// Returns the source position AFTER the fragment.
    /// </summary>
    public static int FindFragmentEndInSource(string cleanMarkdown, string fragment, int searchFromSourcePos = 0)
    {
        if (string.IsNullOrEmpty(fragment) || string.IsNullOrEmpty(cleanMarkdown))
            return -1;

        var (plainText, map) = BuildRenderedToSourceMap(cleanMarkdown);

        var plainIdx = plainText.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
        while (plainIdx >= 0)
        {
            var endPlainIdx = plainIdx + fragment.Length;
            var sourcePos = plainIdx < map.Length ? map[plainIdx] : cleanMarkdown.Length;
            var sourceEndPos = endPlainIdx < map.Length ? map[endPlainIdx] : cleanMarkdown.Length;
            if (sourcePos >= searchFromSourcePos)
                return sourceEndPos;
            plainIdx = plainText.IndexOf(fragment, plainIdx + 1, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }
}
