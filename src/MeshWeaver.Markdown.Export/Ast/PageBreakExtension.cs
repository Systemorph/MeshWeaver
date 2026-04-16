using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace MeshWeaver.Markdown.Export.Ast;

/// <summary>
/// Markdig extension that recognises explicit page-break markers in the source and
/// emits a <see cref="PageBreakBlock"/> AST node.
/// Recognised markers (on their own line):
/// <list type="bullet">
///   <item><description><c>\newpage</c></description></item>
///   <item><description><c>\pagebreak</c></description></item>
///   <item><description><c>&lt;!-- pagebreak --&gt;</c></description></item>
/// </list>
/// </summary>
public sealed class PageBreakExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<PageBreakParser>())
            pipeline.BlockParsers.Insert(0, new PageBreakParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // No HTML rendering path: the export pipeline consumes the AST directly.
    }
}

/// <summary>AST node for a hard page break.</summary>
public sealed class PageBreakBlock(BlockParser parser) : LeafBlock(parser)
{
    // Marker block — no content.
}

/// <summary>Parser that matches lines containing one of the supported page-break tokens.</summary>
public sealed class PageBreakParser : BlockParser
{
    public override BlockState TryOpen(BlockProcessor processor)
    {
        if (processor.IsCodeIndent) return BlockState.None;

        var line = processor.Line;
        var span = line.AsSpan().Trim();
        if (!IsMarker(span)) return BlockState.None;

        processor.NewBlocks.Push(new PageBreakBlock(this)
        {
            Column = processor.Column,
            Span = new Markdig.Syntax.SourceSpan(processor.Start, processor.Line.End)
        });
        return BlockState.BreakDiscard;
    }

    private static bool IsMarker(ReadOnlySpan<char> span) =>
        span.Equals(@"\newpage", StringComparison.OrdinalIgnoreCase) ||
        span.Equals(@"\pagebreak", StringComparison.OrdinalIgnoreCase) ||
        span.Equals("<!-- pagebreak -->", StringComparison.OrdinalIgnoreCase) ||
        span.Equals("<!--pagebreak-->", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Pipeline builder extension method for fluent registration.</summary>
public static class PageBreakPipelineExtensions
{
    /// <summary>Enables <c>\newpage</c> and <c>&lt;!-- pagebreak --&gt;</c> markers.</summary>
    public static MarkdownPipelineBuilder UsePageBreaks(this MarkdownPipelineBuilder builder)
    {
        builder.Extensions.AddIfNotAlready<PageBreakExtension>();
        return builder;
    }
}
