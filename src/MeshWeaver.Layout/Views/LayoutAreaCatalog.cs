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
        
        return layouts
            .OrderBy(x => x.Title)
            .Aggregate(Controls.LayoutGrid.WithSkin(
                skin => skin
                .WithAdaptiveRendering(true)
                .WithJustify(JustifyContent.Center)
                .WithSpacing(20)), (s, l)
                => s.WithView(new LayoutAreaDefinitionControl(l) { Id = thumbnailBaseUrl },
                    skin => skin.WithLg(4).WithMd(6).WithSm(12)));
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

}
