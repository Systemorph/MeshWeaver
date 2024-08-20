using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;

namespace MeshWeaver.MeshBrowser.ViewModel;

/// <summary>
/// MeshBrowser Catalog area definition.
/// </summary>
public static class MeshBrowserCatalogArea
{
    /// <summary>
    /// Adds the MeshBrowser Catalog view to the layout.
    /// </summary>
    /// <param name="layout">The layout to add Catalog to.</param>
    /// <returns>The updated layout including the MeshBrowser Catalog view.</returns>
    /// <remarks>
    /// This method registers the MeshBrowser Catalog view to the provided layout.
    /// </remarks>
    public static LayoutDefinition AddCatalog(this LayoutDefinition layout)
        => layout.WithView(nameof(Catalog), Catalog)
            .WithNavMenu((menu,_, _) =>
                menu.WithNavLink(
                    nameof(Catalog),
                    new LayoutAreaReference(nameof(Catalog)).ToHref(layout.Hub.Address), 
                    FluentIcons.Grid
                    )
            );

    /// <summary>
    /// Catalog view definition.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the view will be displayed.</param>
    /// <param name="context">The rendering context for generating the view.</param>
    /// <returns>The view representing a catalog of discovered mesh nodes.</returns>
    /// <remarks>
    /// This method constructs the main view of the MeshBrowser - the Catalog.
    /// </remarks>
    public static object Catalog(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return MeshNodes
            .Aggregate(Controls.LayoutGrid,
                (stack, node) =>
                    stack.WithView(GetNodeCard(node), skin => skin.WithXs(4))
            );
    }

    private static UiControl GetNodeCard(MeshNode node) =>
        Controls.Stack
            .AddSkin(Skins.Card)
            .WithView(Controls.H3(node.Name))
            .WithView(Controls.Body(node.Description))
            .WithView(
                node.Tags?.Aggregate(Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(3),
                    (stack, tag) => stack.WithView(Controls.Badge(tag)))
                )
        ;

    private static IEnumerable<MeshNode> MeshNodes =>
    [
        new("Northwind")
        {
            Description = "Sample data domain modelling an e-commerce store",
            Thumbnail = "thumbnail1.jpg",
            Created = DateTime.Now,
            Tags = ["northwind", "domain-model"]
        },
        new("Examples Library")
        {
            Description = "Showcasing the basic functionality of the MeshWeaver",
            Thumbnail = "thumbnail2.jpg",
            Created = DateTime.Now,
            Tags = ["examples", "demo"]
        },
        new("MeshNode 3")
        {
            Description = "Sample description 3",
            Thumbnail = "thumbnail3.jpg",
            Created = DateTime.Now,
            Tags = ["tag5", "tag6"]
        }
    ];
}
