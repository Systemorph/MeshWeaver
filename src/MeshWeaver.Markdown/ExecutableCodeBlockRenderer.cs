using System.Web;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MeshWeaver.Markdown;

/// <summary>
/// HTML renderer for <see cref="ExecutableCodeBlock"/>: emits the optional code display, the kernel
/// result-area placeholder div, mermaid diagrams, and embedded layout areas. An executable block
/// that also shows its code is wrapped in a notebook-cell frame (<see cref="CellClass"/>): the code,
/// the kernel result area directly below it, and — on the frame's bottom edge — a toolbar marker the
/// client renderers turn into a Run affordance. Same reading shape as a Code node's notebook cell.
/// <para>The toolbar marker is emitted LAST, so every client that renders these segments in document
/// order (the Blazor <c>MarkdownHtmlRenderer</c>, the React <c>"toolbar"</c> segment, the React
/// Native <c>RunCell</c>) gets the foot placement without knowing anything about it.</para>
/// </summary>
public class ExecutableCodeBlockRenderer : CodeBlockRenderer
{
    /// <summary>Fence argument that requests the code be displayed (<c>--show-code</c>).</summary>
    public const string ShowCode = "show-code";

    /// <summary>Fence argument that requests the fenced header (language + args) be displayed (<c>--show-header</c>).</summary>
    public const string ShowHeader = "show-header";

    /// <summary>CSS class of the notebook-cell frame wrapping an executable block that shows its code.</summary>
    public const string CellClass = "md-code-cell";

    /// <summary>
    /// CSS class of the cell-toolbar marker div, emitted on the cell's BOTTOM edge (after the code
    /// and the output segment). The Blazor renderer replaces it with the interactive toolbar (Run
    /// button + language badge); non-interactive renderers leave it empty.
    /// </summary>
    public const string CellToolbarClass = "md-code-cell-toolbar";

    /// <summary>CSS class of the output segment holding the kernel result area inside the cell frame.</summary>
    public const string CellOutputClass = "md-code-cell-output";

    /// <summary>Attribute on the toolbar marker carrying the block's submission id (= its result-area name).</summary>
    public const string SubmissionIdAttribute = "data-submission-id";

    /// <summary>Attribute on the toolbar marker carrying the block's fence language.</summary>
    public const string LanguageAttribute = "data-language";

    /// <summary>
    /// Literal placeholder emitted in place of the kernel address; substituted with the real address
    /// once the hosting view knows it (see <c>MarkdownViewLogic.ReplaceKernelPlaceholder</c>).
    /// </summary>
    public const string KernelAddressPlaceholder = "__KERNEL_ADDRESS__";

    /// <summary>
    /// Renders an executable/layout/mermaid code block to HTML, falling back to the base renderer for
    /// plain code blocks.
    /// </summary>
    /// <param name="renderer">The HTML renderer to write to.</param>
    /// <param name="obj">The code block being rendered.</param>
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
        var showsHeader = args.TryGetValue(ShowHeader, out var showHeader) && showHeader is null
                          || bool.TryParse(showHeader, out var sh) && sh;
        var showsCode = args.TryGetValue(ShowCode, out var showCode) && showCode is null
                        || bool.TryParse(showCode, out var sc) && sc;

        // Executable block WITH visible code → notebook-cell frame: code first, the run's output
        // attached below it, and the toolbar (Run) as a composer-style bar on the BOTTOM edge —
        // the same reading shape as a Code node's cell (CodeLayoutAreas.BuildContent, moved there
        // on 2026-07-03 UX feedback: the controls belong at the FOOT of the cell, like a chat
        // composer, not above the code). Executable blocks that hide their code (--execute setup,
        // --render live demos) keep the bare result area.
        var isCell = fenced.SubmitCode is not null && (showsHeader || showsCode);
        if (isCell)
            renderer.Write($"<div class=\"{CellClass}\">");

        if (showsHeader)
        {
            renderer.Write("<div class=\"code-content\">");
            renderer.Write($"<pre><code class='language-{fenced.Info}'>");
            renderer.WriteLine("```" + fenced.Info + $" {fenced.Arguments}");

            renderer.WriteLeafRawLines(obj, true, true);

            renderer.WriteLine("```");
            renderer.Write("</code></pre>");
            renderer.Write("</div>");
        }
        else if (showsCode)
        {
            renderer.Write("<div class=\"code-content\">");
            base.Write(renderer, obj);
            renderer.Write("</div>");
        }

        if (fenced.SubmitCode is not null)
        {
            if (isCell)
                renderer.Write($"<div class=\"{CellOutputClass}\">");
            renderer.Writer.Write(LayoutAreaMarkdownRenderer.GetLayoutAreaDiv(KernelAddressPlaceholder, fenced.SubmitCode.Id, null));
            if (isCell)
                renderer.Write("</div>");
        }

        if (isCell)
        {
            // Toolbar LAST — the composer bar on the bottom edge, below the output segment.
            renderer.Write($"<div class=\"{CellToolbarClass}\" " +
                           $"{SubmissionIdAttribute}=\"{HttpUtility.HtmlAttributeEncode(fenced.SubmitCode!.Id)}\" " +
                           $"{LanguageAttribute}=\"{HttpUtility.HtmlAttributeEncode(fenced.SubmitCode.Language)}\"></div>");
            renderer.Write("</div>");
        }

        renderer.EnsureLine();

        if (string.IsNullOrWhiteSpace(fenced.Arguments))
        {
            if (fenced.Info == "mermaid")
            {
                // HTML-escape the diagram source: Mermaid class diagrams contain '<'
                // (stereotypes `<<enumeration>>`, inheritance `<|--`). Written raw, the
                // browser/HtmlAgilityPack parse those as live tags and the diagram text
                // is destroyed before Mermaid reads it. Escaped, it round-trips through
                // textContent/InnerText decoding back to the literal source.
                renderer.Write("<div class='mermaid'>");
                renderer.EnsureLine();
                renderer.WriteEscape(fenced.Lines.ToString());
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
