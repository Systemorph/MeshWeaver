using Markdig.Renderers;
using Markdig.Renderers.Html;
using System.Web;

namespace MeshWeaver.Markdown;

/// <summary>
/// HTML renderer for <see cref="LayoutAreaComponentInfo"/>: emits an inline layout-area div (for <c>@@</c>)
/// or a unified-content-reference hyperlink (for <c>@</c>), carrying raw-path and resolved address/area/id
/// data attributes for client-side resolution.
/// </summary>
public class LayoutAreaMarkdownRenderer : HtmlObjectRenderer<LayoutAreaComponentInfo>
{
    /// <summary>
    /// Writes the layout-area reference as inline content (<c>@@</c>) or a hyperlink (<c>@</c>).
    /// </summary>
    /// <param name="renderer">The HTML renderer to write to.</param>
    /// <param name="obj">The layout-area reference block being rendered.</param>
    protected override void Write(HtmlRenderer renderer, LayoutAreaComponentInfo obj)
    {
        renderer.EnsureLine();

        // Check if this is a pre-parsed reference (has Area set) vs a raw path reference
        // Pre-parsed: paths with keywords like data:, content:, area: or addressType/addressId/areaName format
        // Raw path: simple paths like @Systemorph that need runtime resolution via IMeshCatalog
        var isPreParsed = obj.Area != null;

        if (obj.IsInline)
        {
            // An @@ embed hides the embedded node's own header + comments by default (see
            // MarkdownOverviewLayoutArea): the embedding page already frames it and the node title
            // duplicated the markdown heading. We pass this as a ?hideHeader=true reference
            // parameter on the raw path; non-markdown areas ignore the unknown parameter, and an
            // author can opt back in with @@node?hideHeader=false.
            var rawPath = WithHideHeader(obj.RawPath);
            if (isPreParsed)
            {
                // Pre-parsed reference - include raw path for Graph resolution + address/area/id
                renderer.WriteLine(GetLayoutAreaDiv(rawPath, obj.Address, obj.Area, obj.Id));
            }
            else
            {
                // Raw path reference - use RawPath for resolution at render time
                renderer.WriteLine(GetLayoutAreaDiv(rawPath));
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

    /// <summary>
    /// Appends the default <c>hideHeader=true</c> reference parameter that inline (@@) embeds use,
    /// unless the author already specified a <c>hideHeader</c> value (preserved as-is). Any
    /// existing query string is kept.
    /// </summary>
    private static string WithHideHeader(string? rawPath)
    {
        var path = rawPath ?? string.Empty;
        if (path.Contains("hideHeader", System.StringComparison.OrdinalIgnoreCase))
            return path;
        return path.Contains('?') ? $"{path}&hideHeader=true" : $"{path}?hideHeader=true";
    }

    /// <summary>CSS class for an embedded layout-area div.</summary>
    public const string LayoutArea = "layout-area";

    /// <summary>CSS class for a unified-content-reference hyperlink.</summary>
    public const string UcrLink = "ucr-link";

    /// <summary>Data-attribute suffix (<c>data-raw-path</c>) carrying the original reference path.</summary>
    public const string RawPath = "raw-path";

    /// <summary>Data-attribute suffix (<c>data-address</c>) carrying the resolved node address.</summary>
    public const string Address = "address";

    /// <summary>Data-attribute suffix (<c>data-area</c>) carrying the resolved area name.</summary>
    public const string Area = "area";

    /// <summary>Data-attribute suffix (<c>data-area-id</c>) carrying the resolved area id.</summary>
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
    /// Creates a layout area div with both raw path and pre-resolved address/area/id.
    /// The raw path enables Graph resolution via IMeshCatalog.ResolvePathAsync(),
    /// while the pre-parsed attributes serve as fallback.
    /// </summary>
    internal static string GetLayoutAreaDiv(string rawPath, object address, string? area, object? id)
        => $"<div class='{LayoutArea}' data-{RawPath}='{HttpUtility.HtmlAttributeEncode(rawPath)}' data-{Address}='{HttpUtility.HtmlAttributeEncode(address?.ToString() ?? string.Empty)}' data-{Area}='{HttpUtility.HtmlAttributeEncode(area ?? string.Empty)}' data-{AreaId}='{HttpUtility.HtmlAttributeEncode(id?.ToString() ?? string.Empty)}'></div>";

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
