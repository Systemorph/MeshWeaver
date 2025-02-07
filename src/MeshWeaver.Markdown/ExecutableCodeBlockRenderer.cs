using Markdig.Helpers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MeshWeaver.Kernel;

namespace MeshWeaver.Markdown;

public class ExecutableCodeBlockRenderer : CodeBlockRenderer
{
    public const string ShowCode = "show-code";
    public const string ShowHeader = "show-header";
    public const string KernelAddressPlaceholder = "__KERNEL_ADDRESS__";

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {

        var fenced = obj as ExecutableCodeBlock;
        if (fenced is null)
        {
            base.Write(renderer, obj);
            return;
        }

        if (string.IsNullOrWhiteSpace(fenced.Arguments))
        {
            if (fenced.Info == "mermaid")
            {
                renderer.Write("<div class='mermaid'>");
                renderer.EnsureLine();
                renderer.Write(fenced.Lines.ToString());
                renderer.EnsureLine();
                renderer.Write("</div>");
            }
            else
                base.Write(renderer, obj);
            return;
        }
        fenced.Initialize();

        renderer.EnsureLine();
        var args = fenced.Args;
        if (args.TryGetValue(ShowHeader, out var showHeader) && showHeader is null || bool.TryParse(showHeader, out var sh) && sh)
        {
            renderer.Write("<div class=\"code-content\">");
            renderer.Write("<pre><code class='language-csharp'>");
            renderer.EnsureLine();
            renderer.WriteLine("```" + fenced.Info + $" {fenced.Arguments}");

            renderer.WriteLeafRawLines(obj, true, true);

            renderer.WriteLine("```");
            renderer.Write("</code></pre>");
            renderer.WriteLine("<div class=\"copy-to-clipboard\"></div>");
            renderer.Write("</div>");
        }
        else if (args.TryGetValue(ShowCode, out var showCode) && showCode is null ||
                 (bool.TryParse(showCode, out var sc) && sc))
        {
            renderer.Write("<div class=\"code-content\">");
            base.Write(renderer, obj);
            renderer.WriteLine("<div class=\"copy-to-clipboard\"></div>");
            renderer.Write("</div>");
        }

        if (fenced.SubmitCode is not null)
            renderer.Writer.Write(LayoutAreaMarkdownRenderer.GetLayoutAreaDiv(KernelAddressPlaceholder, fenced.SubmitCode.Id, null));

        renderer.EnsureLine();
    }


}
