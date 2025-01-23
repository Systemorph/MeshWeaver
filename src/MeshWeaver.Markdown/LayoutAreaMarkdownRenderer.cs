using Markdig.Renderers;
using Markdig.Renderers.Html;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();
        renderer.WriteLine($"<div id='{obj.DivId}' class='layout-area'></div>");
        renderer.EnsureLine();
    }

    public static string LayoutAreaDiv(LayoutAreaComponentInfo obj)
    {
        return $"<div id='{obj.DivId}' class='layout-area' data-address='{obj.Address}' data-area='{obj.Area}'></div>";
    }
}
