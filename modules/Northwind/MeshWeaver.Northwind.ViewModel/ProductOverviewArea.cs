using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class ProductOverviewArea
{
    public static LayoutDefinition AddProductsOverview(this LayoutDefinition layout)
        => 
            layout
                .WithView(nameof(ProductOverview), ProductOverview)
    ;

    public static object ProductOverview(this LayoutAreaHost layoutArea, RenderingContext context) =>
        Controls.Stack
            .WithView(ProductOverviewToolbarArea.ProductOverviewToolbar)
            .WithView((area, ctx) => layoutArea.YearlyDataBySelectedCategory()
                .Select(d =>
                    layoutArea.Workspace
                        .State
                        .Pivot(d.ToDataCube())
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
                ));

    private static IObservable<IEnumerable<NorthwindDataCube>> YearlyDataBySelectedCategory(this LayoutAreaHost layoutArea)
        => layoutArea.YearlyNorthwindData()
            .CombineLatest(
                layoutArea.GetDataStream<ProductOverviewToolbar>(nameof(ProductOverviewToolbar)),
                (data, tb) => (data, tb))
            .Select(x => x.data.Where(d => d.Category == x.tb.Category || x.tb.Category == 0))
    ;

}
