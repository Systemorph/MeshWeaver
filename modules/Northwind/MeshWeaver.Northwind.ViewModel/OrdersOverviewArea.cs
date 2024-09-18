using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

public static class OrdersOverviewArea
{
    public static LayoutDefinition AddOrdersOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(OrdersCount), Controls.Stack.WithView(OrdersCount))
            .WithView(nameof(AvgOrderValue), Controls.Stack.WithView(AvgOrderValue));

    public static IObservable<object> OrdersCount(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a.CountDistinctBy(x => x.OrderId))
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToLineChart(builder => builder)
            );

    public static IObservable<object> AvgOrderValue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
                layoutArea.Workspace
                    .State
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a
                        .WithAggregation(enumerable =>
                        {
                            var list = enumerable.ToList();
                            return (sum: list.Sum(x => x.Amount), count: list.DistinctBy(x => x.OrderId).Count());
                        })
                        .WithResultTransformation(pair => ArithmeticOperations.Divide(pair.sum, pair.count))
                    )
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToLineChart(builder => builder)
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
