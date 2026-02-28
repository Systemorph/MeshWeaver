// <meshweaver>
// Id: DashboardViews
// DisplayName: Dashboard Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Main dashboard for Northwind analytics.
/// </summary>
public static class DashboardViews
{
    public static LayoutDefinition AddDashboardViews(this LayoutDefinition layout) =>
        layout
            .AddLayoutAreaCatalog()
            .WithView(nameof(Dashboard), Dashboard, area => area.WithGroup("Dashboards"))
            .WithView(nameof(DataFiles), DataFiles, area => area.WithGroup("Data"));

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

    /// <summary>
    /// Data Files view showing raw CSV files via FileBrowser.
    /// </summary>
    [Display(GroupName = "Data", Order = 10)]
    public static IObservable<UiControl?> DataFiles(this LayoutAreaHost host, RenderingContext _)
    {
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collectionConfig = contentService?.GetCollectionConfig("Data");

        var fileBrowser = new FileBrowserControl("Data");
        if (collectionConfig != null)
        {
            fileBrowser = fileBrowser.WithCollectionConfiguration(collectionConfig);
        }

        return Observable.Return((UiControl?)Controls.Stack
            .WithView(Controls.Title("Data Files", 1))
            .WithView(Controls.Markdown("CSV data files used by Northwind analytics."))
            .WithView(fileBrowser));
    }
}
