using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Reporting.Models;

namespace MeshWeaver.Northwind.ViewModel;

public record ProductsToolbar(int Category);

public static class ProductsOverviewArea
{
    public static LayoutDefinition AddProductsOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(ProductsOverview), ProductsOverview)
    ;

    public static object ProductsOverview(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return Controls.LayoutGrid
                .WithView(ProductsToolbar, skin => skin.WithXs(12))
                .WithView(ProductsGrid, skin => skin.WithXs(12).WithSm(6))
                .WithView(TopProductsChart, skin => skin.WithXs(12).WithSm(6))
            ;
    }

    public static IObservable<object> ProductsGrid(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetProductsFilteredDataCube()
            .Select(d =>
                layoutArea.Workspace
                    .State
                    .Pivot(d)
                    .SliceRowsBy(nameof(Product))
                    .ToGrid()
                    .WithClass("grid products-grid")
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
