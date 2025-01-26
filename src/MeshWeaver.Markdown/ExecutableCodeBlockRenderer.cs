using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockRenderer : CodeBlockRenderer
{
    private const string HideCode = "hide-code";
    private const string ShowCode = "show-code";
    private const string HideOutput = "hide-output";
    private const string Execute = "execute";
    private const string Mermaid = "mermaid";
    private const string Csharp = "csharp";
    public readonly List<(ExecutionRequest Request, string Div)> ExecutionRequests = new(); 

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        var fenced = obj as FencedCodeBlock;
        if (fenced is null)
        {
            base.Write(renderer, obj);
            return;
        }
        var args = (fenced.Arguments??string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        switch (fenced.Info?.ToLowerInvariant())
        {
            case Mermaid:
                if(HasValue(args, ShowCode))
                    base.Write(renderer, obj);
                var content = string.Join("\n", fenced.Lines.Lines.Select(line => line.ToString()));
                if (!HasValue(args, HideOutput))
                {
                    var mermaid = CreateMermaid(content);
                    renderer.EnsureLine();
                    renderer.WriteLine(mermaid);
                    renderer.EnsureLine();
                }
                break;
            case Csharp:
                if (!HasValue(args, HideCode))
                    base.Write(renderer, obj);
                if (HasValue(args, Execute))
                {
                    content = string.Join("\n", fenced.Lines.Lines.Select(line => line.ToString()));
                    var codeBlockTag = CreateCodeBlock(content, HasValue(args, HideOutput));
                    renderer.EnsureLine();
                    renderer.WriteLine(codeBlockTag);
                    renderer.EnsureLine();
                }

                break;
            default:
                base.Write(renderer, obj);
                break;
        }
    }

    private string CreateMermaid(string content)
    {
        return $"<div class='mermaid'>{content}</div>";
    }

    private static string CreateCodeBlock(string content, bool showOutput) => 
        $"<code-block id='{Guid.NewGuid().AsString()}' data-hide-output='{showOutput}'>{content}</code-block>";

    private static bool HasValue(IEnumerable<string> arguments, string value)
        => arguments.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));


}
