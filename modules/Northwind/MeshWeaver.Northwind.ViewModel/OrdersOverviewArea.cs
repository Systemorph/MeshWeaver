using System.Reactive.Linq;
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
        => layout.WithView(nameof(OrdersCount), Controls.Stack.WithView(OrdersCount));

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

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(1997, 6, 1)));
}
