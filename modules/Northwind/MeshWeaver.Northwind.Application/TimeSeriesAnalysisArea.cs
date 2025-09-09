using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage time series analysis areas in the layout.
/// </summary>
public static class TimeSeriesAnalysisArea
{
    /// <summary>
    /// Adds the time series analysis area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the time series analysis area will be added.</param>
    /// <returns>The updated layout definition with the time series analysis area added.</returns>
    public static LayoutDefinition AddTimeSeriesAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(MonthlySalesTrend), MonthlySalesTrend)
            .WithView(nameof(QuarterlyPerformance), QuarterlyPerformance);

    /// <summary>
    /// Gets the monthly sales trend analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing monthly sales trends.</returns>
    public static UiControl? MonthlySalesTrend(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(TimeSeriesToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new TimeSeriesToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .SelectMany(data =>
                    area.Workspace
                        .Pivot(data.ToDataCube())
                        .WithAggregation(a => a.Sum(x => x.Amount))
                        .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                        .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                        .ToLineChart(builder => builder)
                        .Select(chart => (UiControl)Controls.Stack
                            .WithView(Controls.H2("Monthly Sales Trend"))
                            .WithView(chart.ToControl()))
                ));
    }

    /// <summary>
    /// Gets the quarterly performance analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing quarterly performance.</returns>
    public static IObservable<UiControl> QuarterlyPerformance(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var quarterlyData = data.Select(x => new
                {
                    x.Amount,
                    x.Quantity,
                    x.OrderId,
                    x.Customer,
                    Quarter = $"{x.OrderYear}-Q{((x.OrderDate.Month - 1) / 3) + 1}"
                });

                var quarterlyMetrics = quarterlyData.GroupBy(x => x.Quarter)
                    .Select(g => new
                    {
                        Quarter = g.Key,
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(orderGroup => orderGroup.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.Quarter);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Quarterly Performance Metrics"))
                        .WithView(layoutArea.ToDataGrid(quarterlyMetrics.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            ;
}

/// <summary>
/// Represents a toolbar for time series analysis with year filtering.
/// </summary>
public record TimeSeriesToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}