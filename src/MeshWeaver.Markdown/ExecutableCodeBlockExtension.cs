using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MeshWeaver.Markdown
{
    public class ExecutableCodeBlockExtension : IMarkdownExtension
    {
        public ExecutableCodeBlockRenderer Renderer { get; } = new();

        public void Setup(MarkdownPipelineBuilder pipeline){}
        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
        {
            if (renderer is HtmlRenderer htmlRenderer)
                htmlRenderer.ObjectRenderers.Replace<CodeBlockRenderer>(Renderer);
        }

    }
}
