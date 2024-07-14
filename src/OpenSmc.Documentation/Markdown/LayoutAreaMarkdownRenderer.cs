using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace OpenSmc.Documentation.Markdown
{
    public class LayoutAreaRenderer : HtmlObjectRenderer<LayoutAreaComponentInfo>
    {
        protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
        {
            renderer.EnsureLine();
            renderer.Write($"<div id='{obj.DivId}' class='layout-area'></div>");
        }
    }
}
