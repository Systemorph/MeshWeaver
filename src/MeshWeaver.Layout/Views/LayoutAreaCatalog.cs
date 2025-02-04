using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout.Views;

public static class LayoutAreaCatalogArea
{
    public const string LayoutAreas = nameof(LayoutAreas);

    internal static LayoutDefinition AddLayoutAreaCatalog(this LayoutDefinition layout)
        => layout.WithView(LayoutAreas, LayoutAreaCatalog);

    private static object LayoutAreaCatalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var layouts = host.GetLayoutAreaDefinitions();
        return layouts
            .Where(l => !l.Area.StartsWith("$"))
            .OrderBy(x => x.Title)
            .Aggregate(Controls.LayoutGrid.WithSkin(
                skin => skin
                .WithAdaptiveRendering(true)
                .WithJustify(JustifyContent.Center)
                .WithSpacing(20)), (s, l)
                => s.WithView(new LayoutAreaDefinitionControl(l),
                    skin => skin.WithLg(4).WithMd(6).WithSm(12)));
    }

}
