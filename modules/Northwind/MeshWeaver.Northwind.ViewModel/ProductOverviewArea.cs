using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides methods to add and render the product overview area.
/// </summary>
public static class ProductOverviewArea
{
    /// <summary>
    /// Adds the product overview view to the layout definition.
    /// </summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The updated layout definition with the product overview view.</returns>
    public static LayoutDefinition AddProductOverview(this LayoutDefinition layout)
        =>
            layout
                .WithView(nameof(ProductOverview), ProductOverview)
                .WithNavMenu((menu, _, _) =>
                    menu.WithNavLink(
                        nameof(ProductOverview),
                        new LayoutAreaReference(nameof(ProductOverview)).ToHref(layout.Hub.Address), FluentIcons.Document)
                )
    ;

    /// <summary>
    /// Generates the product overview layout.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An object representing the product overview layout.</returns>
    public static object ProductOverview(this LayoutAreaHost layoutArea, RenderingContext context) =>
        Controls.Stack
            .WithView(ProductOverviewToolbarArea.ProductOverviewToolbar)
            .WithView(ProductGrid);

    private static IObservable<object> ProductGrid(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.ProductOverviewData()
            // .Select(data => data.ToMarkdown())
            .Select(data => 
                layoutArea.ToDataGrid(data.ToArray(),
                    config => config.AutoMapProperties()
                    )
                )
        ;

    private static IObservable<IEnumerable<ProductOverviewItem>> ProductOverviewData(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyDataBySelectedCategory()
            .CombineLatest(layoutArea.Workspace.GetStream(typeof(Category)),
                (data, changeItem) => (data, changeItem))
            .Select(tuple =>
                tuple.data.GroupBy(data => data.Product)
                .Select(g => new ProductOverviewItem
                {
                    ProductId = g.Key,
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault(),
                    CategoryName = tuple.changeItem.Value.GetData<Category>(g.Select(x => x.Category).FirstOrDefault())?.CategoryName,
                    UnitPrice = g.Select(x => x.UnitPrice).FirstOrDefault(),
                    UnitsSold = g.Sum(x => x.Quantity),
                    DiscountGiven = g.Sum(x => x.UnitPrice * x.Quantity * x.Discount),
                    TotalAmount = g.Sum(x => x.Amount),
                })
                .OrderByDescending(x => x.TotalAmount)
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> YearlyDataBySelectedCategory(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .CombineLatest(
                layoutArea.GetDataStream<ProductOverviewToolbar>(nameof(ProductOverviewToolbar)),
                (data, tb) => (data, tb))
            .Select(x => x.data.Where(d => d.Category == x.tb.Category || x.tb.Category == 0))
    ;
}
