using System.Reactive.Linq;
using MeshWeaver.Arithmetics;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Domain;

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
            .Select(data =>
            {
                var topCustomers = data.GroupBy(x => x.Customer)
                    .Select(g => new { Customer = g.Key?.ToString() ?? "Unknown", Revenue = g.Sum(x => x.Amount) })
                    .OrderByDescending(x => x.Revenue)
                    .Take(15)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Bar(topCustomers.Select(c => c.Revenue), "Revenue")
                    .WithLabels(topCustomers.Select(c => c.Customer));

                return Controls.Stack
                    .WithView(Controls.H2("Top 15 Customers by Revenue"))
                    .WithView(chart);
            });

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
                        .WithView(layoutArea.ToDataGrid(customerMetrics.ToArray()))
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
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Customer Order Frequency Distribution"))
                        .WithView(chart.ToControl()));
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
                        .WithView(layoutArea.ToDataGrid(segmentSummary.ToArray()))
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
                    .Select(chart => (UiControl)Controls.Stack
                        .WithView(Controls.H2("Customer Retention Analysis"))
                        .WithView(chart.ToControl()));
            });

    /// <summary>
    /// Toolbar configuration for customer geographic distribution view.
    /// </summary>
    private record CustomerGeographicToolbar
    {
        public const string Map = nameof(Map);
        public const string Table = nameof(Table);

        [UiControl<RadioGroupControl>(Options = new[] { "Table", "Map" })]
        public string Display { get; init; } = Table;
    }

    /// <summary>
    /// Gets the customer geographic distribution.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing customer geographic distribution.</returns>
    public static UiControl? CustomerGeographicDistribution(this LayoutAreaHost host, RenderingContext context)
    {
        return host.Toolbar(new CustomerGeographicToolbar(),
            (toolbar, area, _) => toolbar.Display switch
            {
                CustomerGeographicToolbar.Map => area.CustomerGeographicMap(),
                _ => area.CustomerGeographicTable()
            }
        );
    }

    /// <summary>
    /// Gets the table view for customer geographic data.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing customer geographic table.</returns>
    private static IObservable<UiControl> CustomerGeographicTable(this LayoutAreaHost host)
        => host.GetDataCube()
            .SelectMany(data =>
            {
                var countryData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new
                    {
                        Country = g.Key!,
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Customer Geographic Distribution"))
                        .WithView(host.ToDataGrid(countryData))
                );
            });

    /// <summary>
    /// Gets the map view for customer geographic data.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing customer geographic map.</returns>
    private static IObservable<UiControl> CustomerGeographicMap(this LayoutAreaHost host)
        => host.GetDataCube()
            .SelectMany(data =>
            {
                var countryData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new
                    {
                        Country = g.Key!,
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Customer Geographic Distribution"))
                        .WithView(CreateCustomerMap(countryData))
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
                        .WithView(layoutArea.ToDataGrid(behaviorData.ToArray()))
                );
            });

    private static UiControl CreateCustomerMap(object[] countryData)
    {
        // Country coordinates mapping (major countries from Northwind)
        var countryCoordinates = new Dictionary<string, LatLng>
        {
            ["USA"] = new(39.8283, -98.5795),
            ["Germany"] = new(51.1657, 10.4515),
            ["France"] = new(46.2276, 2.2137),
            ["UK"] = new(55.3781, -3.4360),
            ["Brazil"] = new(-14.2350, -51.9253),
            ["Canada"] = new(56.1304, -106.3468),
            ["Italy"] = new(41.8719, 12.5674),
            ["Spain"] = new(40.4637, -3.7492),
            ["Sweden"] = new(60.1282, 18.6435),
            ["Norway"] = new(60.4720, 8.4689),
            ["Denmark"] = new(56.2639, 9.5018),
            ["Netherlands"] = new(52.1326, 5.2913),
            ["Belgium"] = new(50.5039, 4.4699),
            ["Switzerland"] = new(46.8182, 8.2275),
            ["Austria"] = new(47.5162, 14.5501),
            ["Ireland"] = new(53.1424, -7.6921),
            ["Finland"] = new(61.9241, 25.7482),
            ["Poland"] = new(51.9194, 19.1451),
            ["Portugal"] = new(39.3999, -8.2245),
            ["Mexico"] = new(23.6345, -102.5528),
            ["Argentina"] = new(-38.4161, -63.6167),
            ["Venezuela"] = new(6.4238, -66.5897)
        };

        var markers = new List<MapMarker>();
        var validCountries = new List<dynamic>();
        
        foreach (var country in countryData)
        {
            var countryName = country.GetType().GetProperty("Country")?.GetValue(country)?.ToString() ?? "";
            var customerCount = country.GetType().GetProperty("CustomerCount")?.GetValue(country) ?? 0;
            var revenue = country.GetType().GetProperty("TotalRevenue")?.GetValue(country) ?? 0;
            
            if (countryCoordinates.TryGetValue(countryName, out var coordinates))
            {
                validCountries.Add(country);
                markers.Add(new MapMarker
                {
                    Position = coordinates,
                    Title = $"{countryName}: {customerCount} customers, ${revenue:N0}",
                    Label = countryName.Substring(0, Math.Min(2, countryName.Length)),
                    Data = country
                });
            }
        }

        var mapOptions = new MapOptions
        {
            Center = new LatLng(20, 0), // Center on world
            Zoom = 2,
            MapTypeId = "roadmap",
            ZoomControl = true,
            MapTypeControl = true
        };

        return new GoogleMapControl
        {
            Options = mapOptions,
            Markers = markers,
            Height = "500px",
            Width = "100%"
        };
    }

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
