using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates order analytics displays showing monthly order trends and average order values over time.
/// Features interactive line charts with year filtering to track order volume patterns and revenue per order.
/// Provides insights into business growth and customer spending behavior across different time periods.
/// </summary>
public static class OrdersOverviewArea
{
    /// <summary>
    /// Adds the orders overview area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the orders overview area will be added.</param>
    /// <returns>The updated layout definition with the orders overview area added.</returns>
    public static LayoutDefinition AddOrdersOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(OrdersCount), OrdersCount)
            .WithView(nameof(AvgOrderValue), AvgOrderValue);

    /// <summary>
    /// Displays a line chart showing monthly order counts with separate lines for each year.
    /// Features a year filter toolbar and tracks the total number of distinct orders placed each month.
    /// The chart helps visualize seasonal trends and year-over-year growth in order volume.
    /// Each year is represented by a different colored line with data points for each month.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A line chart with year filter toolbar showing monthly order count trends.</returns>
    public static UiControl? OrdersCount(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(OrdersToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new OrdersToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .SelectMany(data =>
                    area.Workspace
                        .Pivot(data.ToDataCube())
                        .WithAggregation(a => a.CountDistinctBy(x => x.OrderId))
                        .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                        .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                        .ToLineChart(builder => builder)
                        .Select(chart => (UiControl)Controls.Stack
                            .WithView(Controls.H2("Monthly Orders Count by Year"))
                            .WithView(chart.ToControl()))
                ));
    }

    /// <summary>
    /// Shows a line chart displaying average order value trends across months and years.
    /// Calculates the mean revenue per order for each month, helping identify spending patterns
    /// and seasonal variations in customer purchase amounts. Each year appears as a separate line
    /// with different colors, making it easy to compare average order values year-over-year.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A line chart showing monthly average order values with year-based color coding.</returns>
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
                    .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                    .ToLineChart(builder => builder)
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Average Order Value Trends by Year"))
                        .WithView(chart.ToControl()))
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}

/// <summary>
/// Represents a toolbar for orders overview with year filtering.
/// </summary>
public record OrdersToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
