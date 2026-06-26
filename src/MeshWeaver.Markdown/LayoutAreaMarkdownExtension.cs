using Markdig;
using Markdig.Renderers;

namespace MeshWeaver.Markdown;

/// <summary>
/// Markdig extension that adds parsing and HTML rendering for layout-area unified content references
/// (the <c>@</c>/<c>@@</c> syntax), resolving relative paths against <paramref name="currentNodePath"/>.
/// </summary>
/// <param name="currentNodePath">The node path used to resolve relative references, or null.</param>
public class LayoutAreaMarkdownExtension(string? currentNodePath = null) : IMarkdownExtension
{
    /// <summary>The block parser that recognises <c>@</c>/<c>@@</c> layout-area references.</summary>
    public LayoutAreaMarkdownParser MarkdownParser { get; } = new(currentNodePath);
    private readonly LayoutAreaMarkdownRenderer layoutAreaRenderer = new();

    /// <summary>
    /// Registers the layout-area block parser with the pipeline.
    /// </summary>
    /// <param name="pipeline">The pipeline builder being configured.</param>
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.BlockParsers.AddIfNotAlready(MarkdownParser);
    }

    /// <summary>
    /// Registers the layout-area HTML renderer when rendering to HTML.
    /// </summary>
    /// <param name="pipeline">The built pipeline.</param>
    /// <param name="renderer">The renderer being configured.</param>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
        {
            htmlRenderer.ObjectRenderers.AddIfNotAlready(layoutAreaRenderer);
        }
    }
}
