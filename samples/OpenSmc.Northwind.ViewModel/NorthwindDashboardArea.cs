using OpenSmc.Application.Styles;
using OpenSmc.Layout;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Northwind.ViewModel;

/// <summary>
/// This is an example of a dashboard which can be assembled from different subviews.
/// </summary>
public static class NorthwindDashboardArea
{

    /// <summary>
    /// Adds the Northwind Dashboard to the views.
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddDashboard(this LayoutDefinition layout)
        => layout.WithView(nameof(Dashboard), Dashboard, 
            options => options
                .WithMenu(Controls.NavLink(nameof(Dashboard), FluentIcons.Grid,
                    layout.ToHref(new(nameof(Dashboard)))))

            );




    /// <summary>
    /// This is the main dashboard view. It shows....
    /// </summary>
    /// <source name="mysource"></source>
    /// <param name="layoutArea"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public static object Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.Stack()
            .WithSkin(Skins.Splitter)
            .WithView(
                Controls.Stack()
                    .WithSkin(Skins.LayoutGrid)
                    .WithClass("main-content")
                    .WithView(
                        (area, ctx) =>
                            area.OrderSummary(ctx)
                                .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.ProductSummary(ctx)
                                .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.CustomerSummary(ctx)
                                .ToLayoutGridItem(item => item.WithXs(12).WithSm(6))
                    )
                    .WithView(
                        (area, ctx) =>
                            area.SupplierSummary(ctx)
                                .ToLayoutGridItem(item => item.WithXs(12))
                    )

                    .ToSplitterPane()
                    .WithClass("main-content-pane")
            );
    }





}
