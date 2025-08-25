using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

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
        fenced.Initialize();

        var args = fenced.Args;
        if (args.TryGetValue(ShowHeader, out var showHeader) && showHeader is null || bool.TryParse(showHeader, out var sh) && sh)
        {
            renderer.Write("<div class=\"code-content\">");
            renderer.Write("<pre><code class='language-csharp'>");
            renderer.WriteLine("```" + fenced.Info + $" {fenced.Arguments}");

            renderer.WriteLeafRawLines(obj, true, true);

            renderer.WriteLine("```");
            renderer.Write("</code></pre>");
            renderer.Write("</div>");
        }
        else if (args.TryGetValue(ShowCode, out var showCode) && showCode is null ||
                 (bool.TryParse(showCode, out var sc) && sc))
        {
            renderer.Write("<div class=\"code-content\">");
            base.Write(renderer, obj);
            renderer.Write("</div>");
        }

        if (fenced.SubmitCode is not null)
            renderer.Writer.Write(LayoutAreaMarkdownRenderer.GetLayoutAreaDiv(KernelAddressPlaceholder, fenced.SubmitCode.Id, null));

        renderer.EnsureLine();

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
        

        // Handle layout blocks separately from executable code blocks
        if (fenced.Info == "layout")
        {
            renderer.EnsureLine();
            
            if (fenced.LayoutAreaComponent is not null)
            {
                renderer.Writer.Write(LayoutAreaMarkdownRenderer.GetLayoutAreaDiv(fenced.LayoutAreaComponent.Address, fenced.LayoutAreaComponent.Area, fenced.LayoutAreaComponent.Id));
            }
            else if (!string.IsNullOrEmpty(fenced.LayoutAreaError))
            {
                // Render error message as a styled div
                renderer.Write("<div class=\"layout-area-error\" style=\"border: 1px solid #e74c3c; background-color: #fdf2f2; color: #c0392b; padding: 12px; border-radius: 4px; margin: 8px 0;\">");
                renderer.Write("<strong>Layout Area Error:</strong> ");
                renderer.WriteEscape(fenced.LayoutAreaError);
                renderer.Write("</div>");
            }
            
            renderer.EnsureLine();
            return;
        }

        renderer.EnsureLine();
    }


}
