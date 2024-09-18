using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Northwind.Domain;

namespace MeshWeaver.Northwind.ViewModel;

public static class ProductOverviewArea
{
    public static LayoutDefinition AddProductOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(ProductOverview), ProductOverview)
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
                    config => config.AutoMapColumns()
                    )
                )
        ;

    private static IObservable<IEnumerable<ProductOverviewItem>> ProductOverviewData(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyDataBySelectedCategory()
            .CombineLatest(layoutArea.Workspace.ReduceToTypes(typeof(Category)),
                (data, changeItem) => (data, changeItem))
            .Select(tuple =>
                tuple.data.GroupBy(data => data.Product)
                .Select(g => new ProductOverviewItem
                {
                    ProductId = g.Key,
                    ProductName = g.Select(x => x.ProductName).FirstOrDefault(),
                    CategoryName = layoutArea.Workspace.GetData<Category>(g.Select(x => x.Category).FirstOrDefault())?.CategoryName,
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
