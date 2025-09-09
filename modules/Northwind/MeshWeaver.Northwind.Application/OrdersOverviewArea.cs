using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
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
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .Select(data =>
                {
                    var monthlyOrders = data.GroupBy(x => x.OrderDate.ToString("yyyy-MM"))
                        .Select(g => new { 
                            Month = g.Key, 
                            OrderCount = g.DistinctBy(x => x.OrderId).Count() 
                        })
                        .OrderBy(x => x.Month)
                        .ToArray();

                    var chart = (UiControl)Charting.Chart.Line(monthlyOrders.Select(m => m.OrderCount), "Order Count")
                        .WithLabels(monthlyOrders.Select(m => DateTime.ParseExact(m.Month + "-01", "yyyy-MM-dd", null).ToString("MMM yyyy")));

                    return Controls.Stack
                        .WithView(Controls.H2("Monthly Orders Count"))
                        .WithView(chart);
                }));
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
        => layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var financialYear = layoutArea.Reference.GetParameterValue("Year");
                var filterYear = financialYear != null && int.TryParse(financialYear, out var year) ? year : data.Max(d => d.OrderYear);
                var filteredData = data.Where(d => d.OrderDate.Year == filterYear);
                var monthlyAvgValues = filteredData.GroupBy(x => x.OrderDate.ToString("yyyy-MM"))
                    .Select(g => new { 
                        Month = g.Key, 
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderBy(x => x.Month)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Line(monthlyAvgValues.Select(m => m.AvgOrderValue), "Average Order Value")
                    .WithLabels(monthlyAvgValues.Select(m => DateTime.ParseExact(m.Month + "-01", "yyyy-MM-dd", null).ToString("MMM yyyy")));

                return Controls.Stack
                    .WithView(Controls.H2("Average Order Value Trends"))
                    .WithView(chart);
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            ;
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
