using Markdig;
using Markdig.Renderers;
using OpenSmc.Messaging;

namespace OpenSmc.Layout.Markdown;

public class LayoutAreaExtension : IMarkdownExtension
{
    public LayoutAreaParser Parser { get; }

    public LayoutAreaExtension(IMessageHub hub)
    {
        Parser = new LayoutAreaParser(hub);
    }

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.BlockParsers.Contains<LayoutAreaParser>())
            pipeline.BlockParsers.Add(Parser);
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        renderer.ObjectRenderers.AddIfNotAlready(new LayoutAreaMarkdownRenderer());
    }
}
