using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates a product overview display showing detailed product performance metrics in a searchable data grid.
/// Features product details including names, categories, unit prices, quantities sold, discounts given, and total revenue.
/// Includes toolbar filtering by category and displays products ranked by total sales amount.
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
    ;

    /// <summary>
    /// Renders a complete product overview interface with category filtering toolbar and product performance grid.
    /// The top section contains category selection controls, while the bottom displays a sortable data grid
    /// showing product names, categories, unit prices, units sold, discount amounts, and total revenue.
    /// Products are automatically sorted by total revenue in descending order.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A stacked layout with toolbar controls and product data grid.</returns>
    public static UiControl ProductOverview(this LayoutAreaHost layoutArea, RenderingContext context) =>
        Controls.Stack
            .WithView(ProductOverviewToolbarArea.ProductOverviewToolbar)
            .WithView(ProductGrid);

    private static IObservable<UiControl> ProductGrid(this LayoutAreaHost layoutArea, RenderingContext context)
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
                    CategoryName = tuple.changeItem.Value!.GetData<Category>(g.Select(x => x.Category).FirstOrDefault())?.CategoryName,
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
            .Select(x => x.data.Where(d => d.Category == x.tb!.Category || x.tb.Category == 0))
    ;
}
