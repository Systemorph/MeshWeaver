using Markdig.Renderers;
using Markdig.Renderers.Html;
using System.Web;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownRenderer : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();

        // Check if this is a pre-parsed reference (has Area set) vs a raw path reference
        // Pre-parsed: paths with keywords like data:, content:, area: or addressType/addressId/areaName format
        // Raw path: simple paths like @Systemorph that need runtime resolution via IMeshCatalog
        var isPreParsed = obj.Area != null;

        if (obj.IsInline)
        {
            if (isPreParsed)
            {
                // Pre-parsed reference - use address/area/id
                renderer.WriteLine(GetLayoutAreaDiv(obj.Address, obj.Area, obj.Id));
            }
            else
            {
                // Raw path reference - use RawPath for resolution at render time
                renderer.WriteLine(GetLayoutAreaDiv(obj.RawPath));
            }
        }
        else
        {
            if (isPreParsed)
            {
                // Pre-parsed reference - use address/area/id for hyperlink, RawPath for display
                renderer.WriteLine(GetLayoutAreaLink(obj.RawPath, obj.Address, obj.Area, obj.Id));
            }
            else
            {
                // Raw path reference - use RawPath for navigation
                renderer.WriteLine(GetLayoutAreaLink(obj.RawPath));
            }
        }
        renderer.EnsureLine();
    }

    public const string LayoutArea = "layout-area";
    public const string UcrLink = "ucr-link";
    public const string RawPath = "raw-path";
    public const string Address = "address";
    public const string Area = "area";
    public const string AreaId = "area-id";

    /// <summary>
    /// Creates a layout area div using raw path for UCR (@@ syntax).
    /// Address resolution happens at render time via IMeshCatalog.
    /// </summary>
    internal static string GetLayoutAreaDiv(string rawPath)
        => $"<div class='{LayoutArea}' data-{RawPath}='{HttpUtility.HtmlAttributeEncode(rawPath)}'></div>";

    /// <summary>
    /// Creates a layout area div with pre-resolved address/area/id.
    /// Used by executable code blocks where address is already known.
    /// </summary>
    internal static string GetLayoutAreaDiv(object address, string? area, object? id)
        => $"<div class='{LayoutArea}' data-{Address}='{HttpUtility.HtmlAttributeEncode(address?.ToString() ?? string.Empty)}' data-{Area}='{HttpUtility.HtmlAttributeEncode(area ?? string.Empty)}' data-{AreaId}='{HttpUtility.HtmlAttributeEncode(id?.ToString() ?? string.Empty)}'></div>";

    /// <summary>
    /// Creates a UCR hyperlink using raw path for runtime resolution via IMeshCatalog.
    /// </summary>
    internal static string GetLayoutAreaLink(string rawPath)
    {
        var href = $"/{rawPath}";
        var displayText = $"@{rawPath}";

        return $"<a href='{href}' class='{UcrLink}' data-{RawPath}='{HttpUtility.HtmlAttributeEncode(rawPath)}' title='{HttpUtility.HtmlAttributeEncode(rawPath)}'>{HttpUtility.HtmlEncode(displayText)}</a>";
    }

    /// <summary>
    /// Creates a UCR hyperlink with pre-resolved address/area/id.
    /// Used for paths with keywords like data:, content:, area:.
    /// </summary>
    /// <param name="rawPath">The original path as written (for display text)</param>
    /// <param name="address">The resolved address</param>
    /// <param name="area">The resolved area name (e.g., "$Content")</param>
    /// <param name="id">The area ID</param>
    internal static string GetLayoutAreaLink(string rawPath, object address, string? area, object? id)
    {
        // Generate href: /{address}/{area}[/{id}]
        // All areas including $Content use the same URL format
        var href = $"/{address}";
        if (!string.IsNullOrEmpty(area))
        {
            href += $"/{HttpUtility.UrlEncode(area)}";
            if (id != null && !string.IsNullOrEmpty(id.ToString()))
            {
                // Encode each path segment separately to preserve '/' as path separators
                var idPath = id.ToString()!;
                var encodedSegments = idPath.Split('/').Select(HttpUtility.UrlEncode);
                href += $"/{string.Join("/", encodedSegments)}";
            }
        }

        // Display text: @ prefix with original path (preserves original syntax like content:logo.svg)
        var displayText = $"@{rawPath}";
        var tooltip = rawPath;

        return $"<a href='{href}' class='{UcrLink}' data-{Address}='{HttpUtility.HtmlAttributeEncode(address.ToString()!)}' data-{Area}='{HttpUtility.HtmlAttributeEncode(area ?? string.Empty)}' data-{AreaId}='{HttpUtility.HtmlAttributeEncode(id?.ToString() ?? string.Empty)}' title='{HttpUtility.HtmlAttributeEncode(tooltip)}'>{HttpUtility.HtmlEncode(displayText)}</a>";
    }
}
