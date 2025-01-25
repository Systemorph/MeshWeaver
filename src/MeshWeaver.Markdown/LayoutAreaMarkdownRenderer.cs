using Markdig.Renderers;
using Markdig.Renderers.Html;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer(object defaultAddress) : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();
        renderer.WriteLine($"<layout-area id='{obj.DivId}' data-address='{obj.Address ?? defaultAddress}' data-area={obj.Area} data-id={obj.Id} />");
        renderer.EnsureLine();
    }

}
