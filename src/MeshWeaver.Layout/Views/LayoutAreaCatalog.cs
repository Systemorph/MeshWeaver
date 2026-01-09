using System.ComponentModel;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Views;

public static class LayoutAreaCatalogArea
{
    public const string LayoutAreas = nameof(LayoutAreas);

    public static LayoutDefinition AddLayoutAreaCatalog(this LayoutDefinition layout)
        => layout.WithView(LayoutAreas, LayoutAreaCatalog);

    [Browsable(false)]
    public static UiControl LayoutAreaCatalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var layouts = host.GetLayoutAreaDefinitions();
        var thumbnailPattern = host.LayoutDefinition.ThumbnailPattern;

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
                        => s.WithView(CreateLayoutAreaControl(l, thumbnailPattern),
                            skin => skin.WithLg(3).WithMd(4).WithSm(6).WithXs(12)));

                return stack
                    .WithView(Controls.H2(categoryTitle))
                    .WithView(categoryGrid)
                    .WithVerticalGap(20);
            });

        return sections;
    }

    private static LayoutAreaDefinitionControl CreateLayoutAreaControl(LayoutAreaDefinition definition, ThumbnailPattern? pattern)
    {
        // If no thumbnail pattern is configured, return control without thumbnail URLs
        // The Blazor view will render a placeholder image instead
        if (pattern is null)
            return new LayoutAreaDefinitionControl(definition);

        var areaName = ExtractAreaName(definition);

        // Generate a simple hash based on current time for cache busting
        // In production, this could be based on file modification time or content hash
        var thumbnailHash = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        return new LayoutAreaDefinitionControl(
            definition,
            pattern.GetLightUrl(areaName),
            pattern.GetDarkUrl(areaName),
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
