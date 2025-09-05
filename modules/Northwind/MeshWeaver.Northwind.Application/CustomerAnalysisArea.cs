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
/// Provides methods to add and manage customer analysis areas in the layout.
/// </summary>
public static class CustomerAnalysisArea
{
    /// <summary>
    /// Adds the customer analysis area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the customer analysis area will be added.</param>
    /// <returns>The updated layout definition with the customer analysis area added.</returns>
    public static LayoutDefinition AddCustomerAnalysis(this LayoutDefinition layout)
        => layout.WithView(nameof(TopCustomersByRevenue), TopCustomersByRevenue)
            .WithView(nameof(CustomerLifetimeValue), CustomerLifetimeValue)
            .WithView(nameof(CustomerOrderFrequency), CustomerOrderFrequency)
            .WithView(nameof(CustomerSegmentation), CustomerSegmentation)
            .WithView(nameof(CustomerRetentionAnalysis), CustomerRetentionAnalysis)
            .WithView(nameof(CustomerGeographicDistribution), CustomerGeographicDistribution)
            .WithView(nameof(CustomerPurchaseBehavior), CustomerPurchaseBehavior);

    /// <summary>
    /// Gets the top customers by revenue chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing top customers by revenue.</returns>
    public static IObservable<UiControl> TopCustomersByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.Amount))
                    .SliceRowsBy(nameof(NorthwindDataCube.Customer))
                    .ToBarChart(builder => builder)
                    .Select(x => x.ToControl())
            );

    /// <summary>
    /// Gets the customer lifetime value analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer lifetime value.</returns>
    public static IObservable<UiControl> CustomerLifetimeValue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var customerMetrics = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = g.Key,
                        TotalRevenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        FirstOrderDate = g.Min(x => x.OrderDate),
                        LastOrderDate = g.Max(x => x.OrderDate),
                        CustomerTenure = (g.Max(x => x.OrderDate) - g.Min(x => x.OrderDate)).TotalDays,
                        AvgOrderValue = g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount))
                    })
                    .Where(x => x.CustomerTenure > 0)
                    .Select(x => new
                    {
                        x.Customer,
                        x.TotalRevenue,
                        x.OrderCount,
                        x.AvgOrderValue,
                        CustomerTenureMonths = x.CustomerTenure / 30.44,
                        MonthlyValue = x.TotalRevenue / Math.Max(x.CustomerTenure / 30.44, 1)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(20);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Customer Lifetime Value Analysis"))
                        .WithView(Controls.DataGrid(customerMetrics.ToArray()))
                );
            });

    /// <summary>
    /// Gets the customer order frequency analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer order frequency.</returns>
    public static IObservable<UiControl> CustomerOrderFrequency(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var frequencyData = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = g.Key,
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .GroupBy(x => x.OrderCount switch
                    {
                        1 => "1 Order",
                        2 => "2 Orders", 
                        >= 3 and <= 5 => "3-5 Orders",
                        >= 6 and <= 10 => "6-10 Orders",
                        >= 11 and <= 20 => "11-20 Orders",
                        _ => "20+ Orders"
                    })
                    .Select(g => new { FrequencyBracket = g.Key, CustomerCount = g.Count() });

                return layoutArea.Workspace
                    .Pivot(frequencyData.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.CustomerCount))
                    .SliceRowsBy("FrequencyBracket")
                    .ToPieChart(builder => builder)
                    .Select(x => x.ToControl());
            });

    /// <summary>
    /// Gets the customer segmentation analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer segmentation.</returns>
    public static IObservable<UiControl> CustomerSegmentation(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var customerSegments = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = g.Key,
                        TotalRevenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        AvgOrderValue = g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount))
                    })
                    .Select(x => new
                    {
                        x.Customer,
                        x.TotalRevenue,
                        x.OrderCount,
                        x.AvgOrderValue,
                        Segment = (x.TotalRevenue, x.OrderCount) switch
                        {
                            var (revenue, orders) when revenue >= 5000 && orders >= 10 => "VIP",
                            var (revenue, orders) when revenue >= 2000 && orders >= 5 => "High Value",
                            var (revenue, orders) when revenue >= 1000 || orders >= 3 => "Regular",
                            var (revenue, orders) when revenue >= 500 || orders >= 2 => "Occasional",
                            _ => "New"
                        }
                    });

                var segmentSummary = customerSegments.GroupBy(x => x.Segment)
                    .Select(g => new
                    {
                        Segment = g.Key,
                        CustomerCount = g.Count(),
                        TotalRevenue = g.Sum(x => x.TotalRevenue),
                        AvgRevenue = g.Average(x => x.TotalRevenue)
                    });

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.Markdown("""
                        ## Customer Segmentation Analysis
                        
                        Customer segments based on revenue and order frequency:
                        - **VIP**: $5,000+ revenue AND 10+ orders
                        - **High Value**: $2,000+ revenue AND 5+ orders  
                        - **Regular**: $1,000+ revenue OR 3+ orders
                        - **Occasional**: $500+ revenue OR 2+ orders
                        - **New**: Below occasional thresholds
                        """))
                        .WithView(Controls.H3("Customer Segment Summary"))
                        .WithView(Controls.DataGrid(segmentSummary.ToArray()))
                );
            });

    /// <summary>
    /// Gets the customer retention analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer retention analysis.</returns>
    public static IObservable<UiControl> CustomerRetentionAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var customerMonthlyActivity = data.GroupBy(x => new { x.Customer, x.OrderMonth })
                    .Select(g => new { g.Key.Customer, g.Key.OrderMonth })
                    .GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = g.Key,
                        ActiveMonths = g.Select(x => x.OrderMonth).Distinct().Count(),
                        FirstMonth = g.Min(x => x.OrderMonth),
                        LastMonth = g.Max(x => x.OrderMonth)
                    });

                var retentionMetrics = customerMonthlyActivity.GroupBy(x => x.ActiveMonths)
                    .Select(g => new
                    {
                        ActiveMonthsRange = g.Key switch
                        {
                            1 => "1 Month",
                            2 => "2 Months",
                            >= 3 and <= 6 => "3-6 Months",
                            >= 7 and <= 12 => "7-12 Months",
                            _ => "12+ Months"
                        },
                        CustomerCount = g.Count()
                    })
                    .GroupBy(x => x.ActiveMonthsRange)
                    .Select(g => new { RetentionPeriod = g.Key, CustomerCount = g.Sum(x => x.CustomerCount) });

                return layoutArea.Workspace
                    .Pivot(retentionMetrics.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.CustomerCount))
                    .SliceRowsBy("RetentionPeriod")
                    .ToBarChart(builder => builder)
                    .Select(x => x.ToControl());
            });

    /// <summary>
    /// Gets the customer geographic distribution.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer geographic distribution.</returns>
    public static IObservable<UiControl> CustomerGeographicDistribution(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var countryData = data.GroupBy(x => x.ShipCountry)
                    .Select(g => new
                    {
                        Country = g.Key ?? "Unknown",
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        TotalRevenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Customer Geographic Distribution"))
                        .WithView(Controls.DataGrid(countryData.ToArray()))
                );
            });

    /// <summary>
    /// Gets the customer purchase behavior analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer purchase behavior.</returns>
    public static IObservable<UiControl> CustomerPurchaseBehavior(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var behaviorData = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = g.Key,
                        PreferredCategories = g.GroupBy(x => x.Category)
                            .OrderByDescending(cat => cat.Sum(x => x.Amount))
                            .Take(2)
                            .Select(cat => cat.Key.ToString())
                            .ToList(),
                        AvgDiscount = g.Average(x => x.Discount),
                        PreferredOrderMonth = g.GroupBy(x => x.OrderMonth)
                            .OrderByDescending(month => month.Count())
                            .First().Key,
                        TotalRevenue = g.Sum(x => x.Amount)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(20);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Customer Purchase Behavior Analysis"))
                        .WithView(Controls.DataGrid(behaviorData.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}