using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockRenderer : CodeBlockRenderer
{
    private const string HideCode = "hide-code";
    private const string Execute = "execute";

    public readonly List<(ExecutionRequest Request, string Div)> ExecutionRequests = new(); 

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var fenced = obj as FencedCodeBlock;
        if (fenced is null || string.IsNullOrWhiteSpace(fenced.Arguments))
        {
            base.Write(renderer, obj);
            return;
        }

        var args = fenced.Arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if(!HasValue(args, HideCode))
            base.Write(renderer, obj);


        if (HasValue(args, Execute))
        {
            var content = string.Join("\n", fenced.Lines.Lines.Select(line => line.ToString()));

            var codeBlockTag = CreateCodeBlock(content);
            renderer.EnsureLine();
            renderer.WriteLine(codeBlockTag);
            renderer.EnsureLine();

        }
    }

    private static string CreateCodeBlock(string content)
    {
        var codeBlockTag = $"<CodeBlock id='{Guid.NewGuid().AsString()}'>{content}</CodeBlock>";
        return codeBlockTag;
    }

    private static bool HasValue(IEnumerable<string> arguments, string value)
        => arguments.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));


}
