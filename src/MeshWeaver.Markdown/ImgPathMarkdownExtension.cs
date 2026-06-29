using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeshWeaver.Markdown;

/// <summary>
/// Markdig extension that rewrites every image URL in a document through a transformation function
/// (e.g. to map a relative path to a static-content href).
/// </summary>
/// <param name="transformation">Maps an original image URL to its rewritten URL.</param>
public class ImgPathMarkdownExtension(Func<string, string> transformation) : IMarkdownExtension
{
    /// <summary>
    /// Hooks the image-path rewrite into the pipeline's document-processed event.
    /// </summary>
    /// <param name="pipeline">The pipeline builder being configured.</param>
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed += ChangeImgPath;
    }

    /// <summary>
    /// Applies <c>transformation</c> to the URL of every image link in the document.
    /// </summary>
    /// <param name="document">The parsed markdown document to rewrite in place.</param>
    public void ChangeImgPath(Markdig.Syntax.MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link is { IsImage: true, Url: not null })
                link.Url = transformation.Invoke(link.Url);
        }
    }

    /// <summary>
    /// No-op: this extension operates on the parsed document, not the renderer.
    /// </summary>
    /// <param name="pipeline">The built pipeline.</param>
    /// <param name="renderer">The renderer being configured.</param>
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
