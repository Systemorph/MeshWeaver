using Markdig;
using Markdig.Renderers;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Markdown;

public class LayoutAreaMarkdownExtension(IMessageHub hub) : IMarkdownExtension
{
    public LayoutAreaMarkdownParser MarkdownParser { get; } = new(hub);

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<LayoutAreaMarkdownParser>())
            pipeline.BlockParsers.Insert(0,MarkdownParser);
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer htmlRenderer)
            htmlRenderer.ObjectRenderers.Add(new LayoutAreaMarkdownRenderer());
    }
}
