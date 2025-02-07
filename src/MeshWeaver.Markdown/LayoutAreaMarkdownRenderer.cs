using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer() : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();
        renderer.WriteLine(GetLayoutAreaDiv(obj.Address, obj.Area, obj.Id));
        renderer.EnsureLine();
    }

    public const string LayoutArea = "layout-area";
    public const string Address = "address";
    public const string Area = "area";
    public const string AreaId = "area-id";

    internal static string GetLayoutAreaDiv(object address, string area, object id)
        => $"<div class='{LayoutArea}' data-{Address}='{address}' data-{Area}={area} data-{AreaId}={id} ></div>";
}
