using Markdig;
using Markdig.Renderers;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownExtension() : IMarkdownExtension
{
    public LayoutAreaMarkdownParser MarkdownParser { get; } = new();
    private readonly LayoutAreaMarkdownRenderer layoutAreaRenderer = new();
    private readonly DataContentBlockRenderer dataContentRenderer = new();
    private readonly FileContentBlockRenderer fileContentRenderer = new();

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.BlockParsers.AddIfNotAlready(MarkdownParser);
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
        {
            htmlRenderer.ObjectRenderers.AddIfNotAlready(layoutAreaRenderer);
            htmlRenderer.ObjectRenderers.AddIfNotAlready(dataContentRenderer);
            htmlRenderer.ObjectRenderers.AddIfNotAlready(fileContentRenderer);
        }
    }
}
