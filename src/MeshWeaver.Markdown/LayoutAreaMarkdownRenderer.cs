using Markdig.Renderers;
using Markdig.Renderers.Html;
using System.Web;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();
        if (obj.IsInline)
        {
            // @@ syntax: render inline content (current behavior)
            renderer.WriteLine(GetLayoutAreaDiv(obj.Address, obj.Area, obj.Id));
        }
        else
        {
            // @ syntax: render as hyperlink
            renderer.WriteLine(GetLayoutAreaLink(obj.Address, obj.Area, obj.Id));
        }
        renderer.EnsureLine();
    }

    public const string LayoutArea = "layout-area";
    public const string UcrLink = "ucr-link";
    public const string Address = "address";
    public const string Area = "area";
    public const string AreaId = "area-id";

    internal static string GetLayoutAreaDiv(object address, string? area, object? id)
        => $"<div class='{LayoutArea}' data-{Address}='{address}' data-{Area}='{area ?? string.Empty}' data-{AreaId}='{id}' ></div>";

    internal static string GetLayoutAreaLink(object address, string? area, object? id)
    {
        // Generate href: /{address}/{area}[/{id}]
        var href = $"/{address}";
        if (!string.IsNullOrEmpty(area))
        {
            href += $"/{HttpUtility.UrlEncode(area)}";
            if (id != null && !string.IsNullOrEmpty(id.ToString()))
                href += $"/{HttpUtility.UrlEncode(id.ToString()!)}";
        }

        // Display text: prefer area name, then id, then address
        var displayText = area ?? id?.ToString() ?? address.ToString();
        var tooltip = string.IsNullOrEmpty(area) ? address.ToString() : $"{area}: {address}";

        return $"<a href='{href}' class='{UcrLink}' data-{Address}='{HttpUtility.HtmlAttributeEncode(address.ToString()!)}' data-{Area}='{HttpUtility.HtmlAttributeEncode(area ?? string.Empty)}' data-{AreaId}='{HttpUtility.HtmlAttributeEncode(id?.ToString() ?? string.Empty)}' title='{HttpUtility.HtmlAttributeEncode(tooltip!)}'>{HttpUtility.HtmlEncode(displayText)}</a>";
    }
}
