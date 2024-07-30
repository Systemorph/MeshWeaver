using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;
using OpenSmc.Layout.Domain;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// Defines a static class for creating and managing the Northwind Dashboard within the OpenSmc.Northwind.ViewModel namespace. This class provides methods to add the dashboard to a layout and to generate the main dashboard view.
/// </summary>
public static class NorthwindDashboardArea
{

    /// <summary>
    /// Adds the Northwind Dashboard view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the Northwind Dashboard view.</returns>
    /// <remarks>This method enhances the provided layout definition by adding a navigation link to the Northwind Dashboard view, using the FluentIcons.Grid icon for the menu. It configures the dashboard view's appearance and behavior within the application's navigation structure.
    /// </remarks>
    public static LayoutDefinition AddDashboard(this LayoutDefinition layout)
        => layout.WithView(nameof(Dashboard), Dashboard)
            .WithNavMenu((menu,_) =>
                menu.WithNavLink(
                    nameof(Dashboard), FluentIcons.Grid,
                    new LayoutAreaReference(nameof(Dashboard)).ToHref(layout.Hub))
            );




    /// <summary>
    /// Generates the main dashboard view for a given layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the dashboard view will be displayed.</param>
    /// <param name="context">The rendering context for generating the view.</param>
    /// <returns>A dynamically generated view object representing the Northwind Dashboard.</returns>
    /// <remarks>
    /// This method constructs the main view of the Northwind Dashboard, incorporating various subviews and components to provide a comprehensive overview of the application's data and functionality. The specific contents and layout of the dashboard are determined at runtime based on the rendering context.
    /// </remarks>
    
    public static object Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.Splitter
            .WithView(
                Controls.Stack
                    .WithSkin(Skins.LayoutGrid.WithClass("main-content"))
                    .WithView(
                        (area, ctx) =>
                            area.OrderSummary(ctx)
                                .WithSkin(Skins.LayoutGridItem.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.ProductSummary(ctx)
                                .WithSkin(Skins.LayoutGridItem.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.CustomerSummary(ctx)
                                .WithSkin(Skins.LayoutGridItem.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.SupplierSummary(ctx)
                                .WithSkin(Skins.LayoutGridItem.WithXs(12))
                    )
                    .WithSkin(Skins.SplitterPane.WithClass("main-content-pane"))
            );
    }





}
