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
/// <param name="currentNodePath">The path of the node this markdown belongs to, or null. Only the
/// ```` ```prompt ```` fence uses it — see <see cref="WritePromptComposer"/>.</param>
public class ExecutableCodeBlockRenderer(string? currentNodePath = null) : CodeBlockRenderer
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

    /// <summary>
    /// CSS class of the cell's CODE segment. Inside a cell frame it doubles as a marker: it carries
    /// the same <see cref="SubmissionIdAttribute"/> / <see cref="LanguageAttribute"/> the toolbar
    /// marker carries, so a client renderer can swap the static <c>&lt;pre&gt;</c> for a live editor
    /// exactly the way it already swaps the toolbar marker for the Run bar (#1636). A client that
    /// does not (React, React Native, any static HTML consumer) sees an ordinary div and keeps
    /// rendering the fence read-only — the attributes are additive.
    /// </summary>
    public const string CellCodeClass = "code-content";

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

        if (fenced.PromptDraft is not null && WritePromptComposer(renderer, obj, fenced.PromptDraft))
            return;

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
            renderer.Write(OpenCodeSegment(fenced, isCell));
            renderer.Write($"<pre><code class='language-{fenced.Info}'>");
            renderer.WriteLine("```" + fenced.Info + $" {fenced.Arguments}");

            renderer.WriteLeafRawLines(obj, true, true);

            renderer.WriteLine("```");
            renderer.Write("</code></pre>");
            renderer.Write("</div>");
        }
        else if (showsCode)
        {
            renderer.Write(OpenCodeSegment(fenced, isCell));
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


    /// <summary>
    /// Lowers a ```` ```prompt ```` fence (#2511) onto the layout-area marker for the page node's own
    /// <see cref="PromptFence.AreaName"/> area — the composer, pre-filled with the authored prompt
    /// and editable in place, whose Submit starts a real agent thread and opens it full-page.
    ///
    /// <para>It reuses the marker every client ALREADY hydrates rather than minting a new one, so no
    /// client has to learn anything for the composer to appear; the authored prompt rides as the
    /// area's reference id (base64url — see <see cref="PromptFence.EncodeDraft"/>).</para>
    ///
    /// <para>🚨 The degradation rule. The marker WRAPS the ordinary read-only fenced block: a client
    /// that hydrates layout areas replaces the div and drops its children (Blazor's
    /// <c>MarkdownHtmlRenderer.RenderLayoutArea</c>), and one that does not renders the children —
    /// the authored prompt, exactly as it read before this extension existed. A fence must never
    /// render as LESS than it did before, and the platform's own tests are the only place that can
    /// see it, because every renderer lives in MeshWeaver.Plugins. See
    /// <c>Doc/Architecture/MarkdownFenceExtensions</c>.</para>
    ///
    /// <para>Returns false — leaving the caller to render the plain fence — when the document has no
    /// owning node. The composer is an area on THAT node's hub, so with no owner there is no address
    /// to point at, and an empty <c>data-address</c> is the ownerless NotFound-storm shape the
    /// kernel areas are gated against for the same reason.</para>
    /// </summary>
    private bool WritePromptComposer(HtmlRenderer renderer, CodeBlock obj, string draft)
    {
        if (string.IsNullOrEmpty(currentNodePath))
            return false;

        renderer.EnsureLine();
        renderer.Write(LayoutAreaMarkdownRenderer.GetLayoutAreaDivOpenTag(
            currentNodePath, PromptFence.AreaName, PromptFence.EncodeDraft(draft)));
        base.Write(renderer, obj);
        renderer.Write("</div>");
        renderer.EnsureLine();
        return true;
    }

    /// <summary>
    /// The opening tag of the cell's code segment. Inside a cell frame it carries the submission id
    /// and language — the marker a client needs to hydrate the segment into an editor; outside one
    /// (a bare <c>--show-code</c> block with nothing to run) it stays a plain styling div, because
    /// there is no submission to address and nothing to persist an edit into.
    /// </summary>
    private static string OpenCodeSegment(ExecutableCodeBlock fenced, bool isCell) =>
        isCell
            ? $"<div class=\"{CellCodeClass}\" " +
              $"{SubmissionIdAttribute}=\"{HttpUtility.HtmlAttributeEncode(fenced.SubmitCode!.Id)}\" " +
              $"{LanguageAttribute}=\"{HttpUtility.HtmlAttributeEncode(fenced.SubmitCode.Language)}\">"
            : $"<div class=\"{CellCodeClass}\">";
}
