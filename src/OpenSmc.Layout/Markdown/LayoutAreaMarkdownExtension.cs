using Markdig;
using Markdig.Renderers;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Markdown;

public class LayoutAreaMarkdownExtension(IMessageHub hub) : IMarkdownExtension
{
    public LayoutAreaMarkdownParser MarkdownParser { get; } = new(hub);
    private readonly LayoutAreaMarkdownRenderer layoutAreaRenderer = new();

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
