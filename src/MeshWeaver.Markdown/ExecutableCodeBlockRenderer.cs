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
        if (fenced is null || string.IsNullOrWhiteSpace(fenced.Arguments))
        {
            base.Write(renderer, obj);
            return;
        }

        renderer.EnsureLine();
        var args = fenced.Args;
        if (args.TryGetValue(ShowHeader, out var showHeader) && bool.TryParse(showHeader, out var sh) && sh)
        {
            var orig = obj.Lines;
            obj.Lines = new(obj.Lines.Count + 2);
            var sl = new StringSlice("```" + fenced.Info + $" {fenced.Arguments}");
            obj.Lines.Add(new StringLine(ref sl));
            foreach (var line in orig.Lines)
                obj.Lines.Add(line);
            sl = new StringSlice("```");
            obj.Lines.Add(new StringLine(ref sl));
            base.Write(renderer, obj);
            obj.Lines = orig;
        }
        else if (args.TryGetValue(ShowCode, out var showCode) && bool.TryParse(showCode, out var sc) && sc)
            base.Write(renderer, obj);

        if (fenced.SubmitCode is not null)
            LayoutAreaMarkdownRenderer.GetLayoutAreaDiv(KernelAddressPlaceholder, fenced.SubmitCode.Id, null);

        renderer.EnsureLine();
    }


}
