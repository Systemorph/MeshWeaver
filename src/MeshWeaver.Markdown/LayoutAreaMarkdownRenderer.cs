using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer(object defaultAddress) : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();
        renderer.WriteLine($"<div class ='{LayoutArea}' id='{obj.DivId}' data-{Address}='{obj.Address ?? defaultAddress}' data-{Area}={obj.Area} data-{AreaId}={obj.Id} ></div>");
        renderer.EnsureLine();
    }

    public const string LayoutArea = "layout-area";
    public const string Address = "address";
    public const string Area = "area";
    public const string AreaId = "area-id";
}
