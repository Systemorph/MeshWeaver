// <meshweaver>
// Id: DashboardViews
// DisplayName: Dashboard Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;

/// <summary>
/// Main dashboard for Northwind analytics.
/// </summary>
public static class DashboardViews
{
    public static LayoutDefinition AddDashboardViews(this LayoutDefinition layout) =>
        layout
            .AddLayoutAreaCatalog()
            .WithView(nameof(Dashboard), Dashboard, area => area.WithGroup("Dashboards"));

    /// <summary>
    /// Main dashboard with 4-panel grid: Orders Summary, Sales by Category, Supplier Summary, Top Products.
    /// </summary>
    [Display(GroupName = "Dashboards", Order = 10)]
    public static UiControl Dashboard(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.LayoutGrid
            .WithClass("main-content")
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Top 5 Orders Summary"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(OrderViews.OrderSummary))),
                skin => skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Sales by Category"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(SalesViews.SalesByCategory))),
                skin => skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Supplier Summary"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(SupplierViews.SupplierSummary)).WithStyle(s => s.WithWidth("100%"))),
                skin => skin.WithXs(12).WithSm(6)
            )
            .WithView(
                Controls.Stack
                    .WithView(Controls.PaneHeader("Top Products"))
                    .WithView(Controls.LayoutArea(layoutArea.Hub.Address, nameof(ProductViews.ProductOverview))),
                skin => skin.WithXs(12).WithSm(6)
            );
    }
}
