using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeshWeaver.Markdown;

/// <summary>
/// Strips leading '@' from regular markdown link URLs and resolves relative paths.
/// Allows authors to write [text](@Path/To/Node) which gets resolved as a normal link.
/// Relative paths are resolved against the current node path.
/// Absolute paths start with '/'.
/// </summary>
public class LinkUrlCleanupExtension(string? currentNodePath = null) : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed += ResolveLinks;
    }

    private void ResolveLinks(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link is not { IsImage: false, Url: not null })
                continue;

            var url = link.Url;

            // Strip leading '@' prefix
            if (url.StartsWith('@'))
                url = url.TrimStart('@');

            // Skip external links, anchors, and mailto
            if (url.StartsWith("http") || url.StartsWith('#') || url.StartsWith("mailto:"))
            {
                link.Url = url;
                continue;
            }

            // Handle fragment on internal links (e.g., "MeshGraph#section")
            string? fragment = null;
            var hashIndex = url.IndexOf('#');
            if (hashIndex >= 0)
            {
                fragment = url[hashIndex..];
                url = url[..hashIndex];
            }

            if (url.StartsWith('/'))
            {
                // Already absolute — keep as-is
                link.Url = url + fragment;
            }
            else if (!string.IsNullOrEmpty(url))
            {
                // Relative — resolve against current node path, prepend '/'
                var resolved = PathUtils.ResolveRelativePath(url, currentNodePath);
                link.Url = $"/{resolved}" + fragment;
            }
            else
            {
                // Just a fragment (e.g., "#section")
                link.Url = url + fragment;
            }
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }
}
