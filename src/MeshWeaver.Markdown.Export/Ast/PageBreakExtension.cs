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
    /// <summary>
    /// Registers the <c>PageBreakParser</c> at the front of the pipeline's block parsers so
    /// page-break markers are recognised before other block parsers run.
    /// </summary>
    /// <param name="pipeline">The pipeline builder being configured.</param>
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<PageBreakParser>())
            pipeline.BlockParsers.Insert(0, new PageBreakParser());
    }

    /// <summary>
    /// No-op render setup: the export pipeline consumes the AST directly, so there is no HTML
    /// rendering path to configure.
    /// </summary>
    /// <param name="pipeline">The built markdown pipeline.</param>
    /// <param name="renderer">The renderer that would normally receive custom render hooks.</param>
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
    /// <summary>
    /// Attempts to open a <c>PageBreakBlock</c> when the current line is a recognised
    /// page-break marker.
    /// </summary>
    /// <param name="processor">The block processor positioned at the current line.</param>
    /// <returns>
    /// <c>BlockState.BreakDiscard</c> when a marker was matched and consumed; otherwise
    /// <c>BlockState.None</c>.
    /// </returns>
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
