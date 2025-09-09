using System.ComponentModel;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Views;

public static class LayoutAreaCatalogArea
{
    public const string LayoutAreas = nameof(LayoutAreas);

    internal static LayoutDefinition AddLayoutAreaCatalog(this LayoutDefinition layout)
        => layout.WithView(LayoutAreas, LayoutAreaCatalog);

    [Browsable(false)]
    private static UiControl LayoutAreaCatalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var layouts = host.GetLayoutAreaDefinitions();
        
        // Extract thumbnail base URL from the layout area ID if present
        var thumbnailBaseUrl = ExtractThumbnailBaseUrl(host.Reference.Id?.ToString());
        
        // Group layouts by category, then order within groups
        var groupedLayouts = layouts
            .GroupBy(l => l.Group ?? "General")
            .OrderBy(g => g.Key == "General" ? "ZZ_General" : g.Key) // Put "General" at the end
            .ToArray();
        
        // Create sections for each group
        var sections = groupedLayouts.Aggregate(
            Controls.Stack.WithWidth("100%"),
            (stack, group) =>
            {
                var categoryTitle = group.Key == "General" ? "Other Areas" : group.Key;
                
                var categoryGrid = group
                    .OrderBy(x => x.Order ?? 0)
                    .ThenBy(x => x.Title)
                    .Aggregate(Controls.LayoutGrid.WithStyle(style => style.WithWidth("100%")), (s, l)
                        => s.WithView(CreateLayoutAreaControl(l, thumbnailBaseUrl),
                            skin => skin.WithLg(3).WithMd(4).WithSm(6).WithXs(12)))
                    
                    ;

                return stack
                    .WithView(Controls.H2(categoryTitle))
                    .WithView(categoryGrid)
                    .WithVerticalGap(20); 
            });
        
        return sections;
    }

    private static string? ExtractThumbnailBaseUrl(string? layoutAreaId)
    {
        // Look for thumbnail-base parameter in the layout area ID
        // This could be passed as "someId?thumbnail-base=/content/Northwind/thumbnails"
        if (!string.IsNullOrEmpty(layoutAreaId) && layoutAreaId.Contains("thumbnail-base="))
        {
            var parts = layoutAreaId.Split('?', '&');
            var thumbnailBasePart = parts.FirstOrDefault(p => p.StartsWith("thumbnail-base="));
            if (thumbnailBasePart != null)
            {
                return thumbnailBasePart.Substring("thumbnail-base=".Length);
            }
        }
        
        return null;
    }

    private static LayoutAreaDefinitionControl CreateLayoutAreaControl(LayoutAreaDefinition definition, string? thumbnailBaseUrl)
    {
        string? lightThumbnailUrl = null;
        string? darkThumbnailUrl = null;
        string? thumbnailHash = null;

        // If we have a thumbnail base URL, construct light and dark URLs
        if (!string.IsNullOrEmpty(thumbnailBaseUrl))
        {
            var areaName = ExtractAreaName(definition);
            lightThumbnailUrl = $"{thumbnailBaseUrl.TrimEnd('/')}/{areaName}.png";
            darkThumbnailUrl = $"{thumbnailBaseUrl.TrimEnd('/')}/{areaName}-dark.png";
            
            // Generate a simple hash based on current time for cache busting
            // In production, this could be based on file modification time or content hash
            thumbnailHash = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        }

        return new LayoutAreaDefinitionControl(
            definition,
            lightThumbnailUrl,
            darkThumbnailUrl,
            thumbnailHash
        );
    }

    private static string ExtractAreaName(LayoutAreaDefinition definition)
    {
        // Try to extract area name from URL first
        if (!string.IsNullOrEmpty(definition.Url))
        {
            var pathParts = definition.Url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                // Take the last part as area name
                return pathParts[^1];
            }
        }
        
        // Fallback to title, sanitize for filename
        return definition.Title?.Replace(" ", "").Replace("-", "") ?? "unknown";
    }

}
