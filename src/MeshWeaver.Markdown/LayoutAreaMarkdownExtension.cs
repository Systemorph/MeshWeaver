using Markdig;
using Markdig.Renderers;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownExtension(object defaultAddress) : IMarkdownExtension
{
    public LayoutAreaMarkdownParser MarkdownParser { get; } = new(defaultAddress);
    private readonly LayoutAreaMarkdownRenderer layoutAreaRenderer = new(defaultAddress);

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.BlockParsers.AddIfNotAlready(MarkdownParser);
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
            htmlRenderer.ObjectRenderers.AddIfNotAlready(layoutAreaRenderer);
    }


}
