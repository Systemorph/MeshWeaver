using System.Text;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockRenderer : CodeBlockRenderer
{
    public const string Language = "language";
    public const string RawContent = "raw-content";
    public const string CodeBlock = "code-block";
    public const string Arguments = "arguments";
    public const string ShowCode = "--show-code";
    public readonly List<(ExecutionRequest Request, string Div)> ExecutionRequests = new();

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var fenced = obj as FencedCodeBlock;
        if (fenced is null)
        {
            base.Write(renderer, obj);
            return;
        }

        var htmlStringBuilder = new StringBuilder();
        var localRenderer = new HtmlRenderer(new StringWriter(htmlStringBuilder));
        base.Write(localRenderer, obj);
        var htmlString = htmlStringBuilder.ToString();

        var content = string.Join('\n', fenced.Lines);
        renderer.EnsureLine();
        renderer.Write($"<code class='{CodeBlock}' data-{Language}={fenced.Info} data-{Arguments}='{fenced.Arguments}'  data-{RawContent}='{content}' >{htmlString}</code>");
        renderer.EnsureLine();
    }

}
