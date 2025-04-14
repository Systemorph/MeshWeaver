using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage the orders overview area in the layout.
/// </summary>
public static class OrdersOverviewArea
{
    /// <summary>
    /// Adds the orders overview area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the orders overview area will be added.</param>
    /// <returns>The updated layout definition with the orders overview area added.</returns>
    public static LayoutDefinition AddOrdersOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(OrdersCount), Controls.Stack.WithView(OrdersCount))
            .WithView(nameof(AvgOrderValue), Controls.Stack.WithView(AvgOrderValue));

    /// <summary>
    /// Gets the orders count for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the orders count.</returns>
    public static IObservable<UiControl> OrdersCount(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a.CountDistinctBy(x => x.OrderId))
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToLineChart(builder => builder).Select(x => x.ToControl())

            );

    /// <summary>
    /// Gets the average order value for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the average order value.</returns>
    public static IObservable<UiControl> AvgOrderValue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
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
                    .ToLineChart(builder => builder).Select(x => x.ToControl())
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
