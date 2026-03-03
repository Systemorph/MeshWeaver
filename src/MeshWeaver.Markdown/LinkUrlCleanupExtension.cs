using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeshWeaver.Markdown;

/// <summary>
/// Strips leading '@' from regular markdown link URLs.
/// Allows authors to write [text](@Path/To/Node) which gets
/// resolved as a normal link to Path/To/Node.
/// </summary>
public class LinkUrlCleanupExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed += StripAtPrefix;
    }

    private static void StripAtPrefix(Markdig.Syntax.MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link is { IsImage: false, Url: not null } && link.Url.StartsWith('@'))
                link.Url = link.Url.TrimStart('@');
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
