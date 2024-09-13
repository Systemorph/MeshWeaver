using System.Reactive.Linq;
using MeshWeaver.Charting;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public record ProductsToolbar(int Category);

public static class SalesOverviewArea
{
    /// <summary>
    /// Adds the Sales by Category view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the dashboard view will be added.</param>
    /// <returns>The updated layout definition including the SalesByCategory view.</returns>
    public static LayoutDefinition AddSalesOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(SalesByCategory), Controls.Stack.WithView(SalesByCategory))
                .WithView(nameof(TopProducts), TopProducts)
    ;

    public static IObservable<object> SalesByCategory(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.YearlyNorthwindData()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Category))
                    .ToBarChart(
                        builder => builder
                            .WithOptions(o => o.OrderByValueDescending())
                            .WithChartBuilder(o => 
                                o.WithDataLabels(d => 
                                    d.WithAnchor(DataLabelsAnchor.End)
                                        .WithAlign(DataLabelsAlign.End))
                                )
                    )
                    .WithClass("chart sales-by-category-chart")
            );
    }

    public static LayoutStackControl TopProducts(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.Stack
                .WithView(ProductsToolbar)
                // .WithView((area, context) => area.GetDataStream<ProductsToolbar>(nameof(ProductsToolbar)))
                .WithView(TopProductsChart)
            ;
    }

    private static object ProductsToolbar(this LayoutAreaHost layoutArea, RenderingContext context)
        => Controls.Toolbar.WithView((_, _) =>
                layoutArea.GetProductCategories()
                    .Select(categories =>
                    Template.Bind(new ProductsToolbar(0), nameof(ProductsToolbar),
                        tb =>
                            Controls.Select(tb.Category)
                                .WithOptions(
                                    categories.Select(c => new Option<int>(c.CategoryId, c.CategoryName))
                                        .Prepend(new Option<int>(0, "All"))
                                        .Cast<Option>()
                                        .ToArray()
                                )
                    )
                )
                )
        ;

    private static IObservable<IEnumerable<Category>> GetProductCategories(this LayoutAreaHost layoutArea)
        => layoutArea.Workspace.ReduceToTypes(typeof(Category))
            .DistinctUntilChanged()
            .CombineLatest(layoutArea.GetDataCube()
                    .Select(dataCube => dataCube.GetSlices(nameof(Category))
                        .SelectMany(d => d.Tuple)
                        .Select(tuple => tuple.Value)
                        .Distinct()
                    ),
                (changeItem, values) => (changeItem, values))
            .Select(tp => tp.changeItem.Value.GetData<Category>()
                .Where(c => tp.values.Contains(c.CategoryId))
                .OrderBy(c => c.CategoryName)
            );

    public static IObservable<object> TopProductsChart(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetProductsFilteredDataCube()
            .Select(d => 
                layoutArea.Workspace
                    .State
                    .Pivot(d)
                    .SliceColumnsBy(nameof(Product))
                    .ToBarChart(builder => builder
                        .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        .WithChartBuilder(o =>
                            o
                                .AsHorizontal()
                                .WithDataLabels()
                        )
                    )
                    .WithClass("chart top-products-chart")
            );

    private static IObservable<IDataCube<NorthwindDataCube>> GetDataCube(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .Select(data => data.ToDataCube());

    private static IObservable<IDataCube<NorthwindDataCube>> GetProductsFilteredDataCube(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .CombineLatest(
                layoutArea.GetDataStream<ProductsToolbar>(nameof(ProductsToolbar)),
                (data, tb) => (data, tb))
            .Select(x => x.data.Where(d => d.Category == x.tb.Category || x.tb.Category == 0)
                .ToDataCube())
    ;

}
