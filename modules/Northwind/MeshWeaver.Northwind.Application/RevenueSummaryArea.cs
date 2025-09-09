using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates revenue trend analysis showing monthly sales performance over multiple years.
/// Displays interactive line charts with year filtering to track revenue patterns and identify
/// seasonal trends, growth periods, and performance comparisons across different time frames.
/// </summary>
[Display(GroupName = "Financial", Order = 710)]
public static class RevenueSummaryArea
{
    /// <summary>
    /// Adds a revenue summary view to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the revenue summary view will be added.</param>
    /// <returns>The updated layout definition with the revenue summary view.</returns>
    public static LayoutDefinition AddRevenue(this LayoutDefinition layout)
        => layout.WithView(nameof(RevenueSummary), RevenueSummary);

    /// <summary>
    /// Displays a multi-line chart showing monthly revenue trends with separate lines for each year.
    /// Features a year filter toolbar to focus on specific time periods. Each year is represented
    /// by a different colored line connecting monthly revenue data points. The chart includes
    /// a "Revenue Summary by Year" header and shows exact revenue amounts when hovering over data points.
    /// Helps identify seasonal patterns and year-over-year growth trends.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A line chart with year-based filtering and monthly revenue trend lines.</returns>
    [Display(Name = "Revenue Summary", GroupName = "Financial", Order = 1)]
    public static UiControl? RevenueSummary(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(RevenueToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new RevenueToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .SelectMany(data =>
                    area.Workspace
                        .Pivot(data.ToDataCube())
                        .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                        .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
                        .ToLineChart(builder => builder)
                        .Select(chart => (UiControl)Controls.Stack
                            .WithView(Controls.H2("Revenue Summary by Year"))
                            .WithView(chart.ToControl()))
                ));
    }

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData();
}

/// <summary>
/// Represents a toolbar for revenue summary with year filtering.
/// </summary>
public record RevenueToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
