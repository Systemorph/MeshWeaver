using Markdig.Renderers;
using Markdig.Syntax.Inlines;
using Markdig.Syntax;
using Markdig;

namespace MeshWeaver.Layout.Markdown; 
public class ImgPathMarkdownExtension(Func<string, string> transformation) : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed += ChangeImgPath;
    }

    public void ChangeImgPath(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage)
                link.Url = transformation.Invoke(link.Url);
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
