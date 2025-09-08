using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates the main Northwind Dashboard showing a comprehensive overview of business metrics.
/// Displays key performance indicators including top orders summary, sales by category charts,
/// supplier performance data, and top-selling products in an organized grid layout.
/// </summary>
public static class NorthwindDashboardArea
{
    /// <summary>
    /// Adds the main dashboard view to the layout configuration.
    /// Creates a navigation entry that displays the comprehensive business dashboard.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the Northwind Dashboard view.</returns>
    public static LayoutDefinition AddDashboard(this LayoutDefinition layout)
        => layout.WithView(nameof(Dashboard), Dashboard, area => area.WithCategory("Dashboards"))
            ;

    /// <summary>
    /// Renders the main dashboard displaying four key business metric panels:
    /// - Top 5 Orders Summary: Shows highest value recent orders with customer details
    /// - Sales by Category: Bar chart comparing revenue across product categories 
    /// - Supplier Summary: Performance metrics and statistics for all suppliers
    /// - Top Products: List of best-selling products by revenue with quantities sold
    /// All panels are arranged in a responsive grid layout that adapts to screen size.
    /// </summary>
    /// <param name="layoutArea">The layout area host where the dashboard view will be displayed.</param>
    /// <param name="context">The rendering context for generating the view.</param>
    /// <returns>A grid layout containing four business metric panels with headers and data visualizations.</returns>
    public static UiControl Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.LayoutGrid
            .WithClass("main-content")
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Top 5 Orders Summary"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(OrdersSummaryArea.OrderSummary))),
                skin => skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Sales by category"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(SalesOverviewArea.SalesByCategory))), 
                skin => skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Supplier Summary"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(SupplierSummaryArea.SupplierSummary))),
                    skin=>skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Top products"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(ProductOverviewArea.ProductOverview))),
                skin => skin.WithXs(12).WithSm(6)
            );
    }
}
