using Markdig;
using Markdig.Renderers;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation.Markdown;

public class LayoutAreaExtension : IMarkdownExtension
{
    private readonly IMessageHub hub;

    public LayoutAreaExtension(IMessageHub hub)
    {
        this.hub = hub;
    }

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<LayoutAreaParser>())
            pipeline.BlockParsers.Add(new LayoutAreaParser(hub));
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // Optional: Setup renderer if needed
    }
}
