using System.Reactive.Linq;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Domain;
using LayoutDefinition = MeshWeaver.Layout.Composition.LayoutDefinition;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates comprehensive customer analytics views showing detailed customer behavior, segmentation,
/// lifetime value analysis, geographic distribution maps, purchase patterns, and retention metrics.
/// Provides both tabular data grids and interactive charts to analyze customer performance.
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
    /// Displays a horizontal bar chart showing the top 15 customers ranked by total revenue.
    /// Features a year filter toolbar and shows customer company names with corresponding revenue amounts.
    /// The chart is color-coded and includes data labels for easy comparison of customer performance.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A bar chart with customer names and revenue amounts, plus year filter controls.</returns>
    public static UiControl? TopCustomersByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(CustomerToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new CustomerToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .CombineLatest(area.Workspace.GetStream<Customer>()!)
                .Select(tuple =>
                {
                    var data = tuple.First;
                    var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);
                    
                    var topCustomers = data.GroupBy(x => x.Customer)
                        .Select(g => new { 
                            Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown", 
                            Revenue = g.Sum(x => x.Amount) 
                        })
                        .OrderByDescending(x => x.Revenue)
                        .Take(15)
                        .ToArray();

                    var chart = (UiControl)Charting.Chart.Bar(topCustomers.Select(c => c.Revenue), "Revenue")
                        .WithLabels(topCustomers.Select(c => c.Customer));

                    return Controls.Stack
                        .WithView(Controls.H2("Top 15 Customers by Revenue"))
                        .WithView(chart);
                }));
    }

    /// <summary>
    /// Toolbar configuration for customer lifetime value view.
    /// </summary>
    private record CustomerLifetimeToolbar
    {
        public const string Table = nameof(Table);
        public const string Chart = nameof(Chart);

        [UiControl<RadioGroupControl>(Options = new[] { "Table", "Chart" })]
        public string Display { get; init; } = Table;
    }

    /// <summary>
    /// Shows customer lifetime value metrics with toggle between table and chart views.
    /// Table view displays detailed customer metrics including total revenue, order count, average order value,
    /// customer tenure in months, and calculated monthly value. Chart view shows top 10 customers by monthly value
    /// in a bar chart format. Includes toolbar to switch between visualization types.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>Either a data grid with detailed customer metrics or a bar chart showing monthly customer values.</returns>
    public static UiControl? CustomerLifetimeValue(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.Toolbar(new CustomerLifetimeToolbar(),
            (toolbar, area, _) => toolbar.Display switch
            {
                CustomerLifetimeToolbar.Chart => area.CustomerLifetimeChart(),
                _ => area.CustomerLifetimeTable()
            }
        );
    }

    /// <summary>
    /// Gets the table view for customer lifetime value data.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing customer lifetime value table.</returns>
    private static IObservable<UiControl> CustomerLifetimeTable(this LayoutAreaHost layoutArea)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .SelectMany(tuple =>
            {
                var data = tuple.First;
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);
                
                var customerMetrics = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown",
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        FirstOrderDate = g.Min(x => x.OrderDate),
                        LastOrderDate = g.Max(x => x.OrderDate),
                        CustomerTenure = (g.Max(x => x.OrderDate) - g.Min(x => x.OrderDate)).TotalDays,
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .Where(x => x.CustomerTenure > 0)
                    .Select(x => new
                    {
                        x.Customer,
                        x.TotalRevenue,
                        x.OrderCount,
                        x.AvgOrderValue,
                        CustomerTenureMonths = Math.Round(x.CustomerTenure / 30.44, 1),
                        MonthlyValue = Math.Round(x.TotalRevenue / Math.Max(x.CustomerTenure / 30.44, 1), 2)
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
    /// Gets the chart view for customer lifetime value data.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing customer lifetime value chart.</returns>
    private static IObservable<UiControl> CustomerLifetimeChart(this LayoutAreaHost layoutArea)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);
                
                var customerMetrics = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown",
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        MonthlyValue = Math.Round(g.Sum(x => x.Amount) / Math.Max((g.Max(x => x.OrderDate) - g.Min(x => x.OrderDate)).TotalDays / 30.44, 1), 2)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(10)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Bar(customerMetrics.Select(c => c.MonthlyValue), "MonthlyValue")
                    .WithLabels(customerMetrics.Select(c => c.Customer));

                return Controls.Stack
                    .WithView(Controls.H2("Customer Lifetime Value Analysis"))
                    .WithView(chart);
            });

    /// <summary>
    /// Displays a pie chart showing customer distribution by order frequency brackets.
    /// Segments customers into groups: 1 Order, 2 Orders, 3-5 Orders, 6-10 Orders, 11-20 Orders, and 20+ Orders.
    /// Each segment shows the count and percentage of customers in that frequency range with distinct colors.
    /// Helps identify customer loyalty patterns and repeat purchase behavior.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A pie chart with customer frequency segments and a header title.</returns>
    public static UiControl CustomerOrderFrequency(this LayoutAreaHost layoutArea, RenderingContext context)
        =>
            Controls.Stack.WithView(Controls.H2("Customer Order Frequency"))
                .WithView(
                    layoutArea.GetDataCube()
                        .Select(data =>
                        {
                            var frequencyData = data.GroupBy(x => x.Customer)
                                .Select(g => new
                                {
                                    Customer = g.Key, OrderCount = g.DistinctBy(x => x.OrderId).Count()
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
                                .Select(g => new { FrequencyBracket = g.Key, CustomerCount = g.Count() })
                                .ToArray();

                            return Charting.Chart.Pie(frequencyData.Select(x => x.CustomerCount), "CustomerCount")
                                .WithLabels(frequencyData.Select(x => x.FrequencyBracket))
                                .ToControl();
                        })
                );

    /// <summary>
    /// Shows customer segmentation analysis with customers categorized into VIP, High Value, Regular, Occasional, and New segments.
    /// Includes detailed segmentation criteria explanation and a data grid showing customer count, total revenue,
    /// and average revenue per segment. VIP customers have $5,000+ revenue AND 10+ orders, while other segments
    /// are based on revenue thresholds and order frequency combinations.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>Markdown explanation of segments plus a data grid with segment metrics.</returns>
    public static IObservable<UiControl> CustomerSegmentation(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .SelectMany(tuple =>
            {
                var data = tuple.First;
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);
                
                var customerSegments = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown",
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
                        .WithView(Controls.H2("Customer Segmentation"))
                        .WithView(Controls.Markdown("""
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
    public static IObservable<UiControl> CustomerRetentionAnalysis(this LayoutAreaHost layoutArea,
        RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
            {
                var yearlyRetentionData = data.GroupBy(x => x.OrderYear)
                    .SelectMany(yearGroup => yearGroup
                        .GroupBy(x => new { x.Customer, x.OrderMonth })
                        .Select(g => new { g.Key.Customer, g.Key.OrderMonth })
                        .GroupBy(x => x.Customer)
                        .Select(g => new
                        {
                            Year = yearGroup.Key,
                            Customer = g.Key,
                            ActiveMonths = g.Select(x => x.OrderMonth).Distinct().Count()
                        })
                        .GroupBy(x => x.ActiveMonths switch
                        {
                            1 => "1 Month",
                            2 => "2 Months",
                            >= 3 and <= 6 => "3-6 Months",
                            >= 7 and <= 12 => "7-12 Months",
                            _ => "12+ Months"
                        })
                        .Select(g => new { Year = yearGroup.Key, RetentionPeriod = g.Key, CustomerCount = g.Count() })
                    )
                    .GroupBy(x => x.RetentionPeriod)
                    .ToDictionary(g => g.Key, g => g.GroupBy(x => x.Year).ToDictionary(yg => yg.Key, yg => yg.Sum(x => x.CustomerCount)));

                var orderedPeriods = new[] { "1 Month", "2 Months", "3-6 Months", "7-12 Months", "12+ Months" };
                var years = data.Select(x => x.OrderYear).Distinct().OrderBy(x => x).ToArray();
                
                var datasets = years.Select(year => new
                {
                    Label = year.ToString(),
                    Data = orderedPeriods.Select(period => 
                        yearlyRetentionData.TryGetValue(period, out var yearData) && 
                        yearData.TryGetValue(year, out var count) ? count : 0).ToArray()
                }).ToArray();

                var chart = (UiControl)Charting.Chart.Line(datasets.First().Data, "Customer Count")
                    .WithLabels(orderedPeriods);

                return Controls.Stack
                    .WithView(Controls.H2("Customer Retention Analysis by Year"))
                    .WithView(chart);
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
            .CombineLatest(layoutArea.Workspace.GetStream<Category>()!)
            .CombineLatest(layoutArea.Workspace.GetStream<Customer>()!)
            .Select(tuple =>
            {
                var data = tuple.First.First;
                var categories = tuple.First.Second!.ToDictionary(c => c.CategoryId, c => c.CategoryName);
                var customers = tuple.Second!.ToDictionary(c => c.CustomerId, c => c.CompanyName);
                
                var behaviorData = data.GroupBy(x => x.Customer)
                    .Select(g => new
                    {
                        Customer = customers.TryGetValue(g.Key?.ToString() ?? "", out var name) ? name : g.Key?.ToString() ?? "Unknown",
                        PreferredCategories = string.Join(", ",g.GroupBy(x => x.Category)
                            .OrderByDescending(cat => cat.Sum(x => x.Amount))
                            .Take(2)
                            .Select(cat => categories[cat.Key])
                            ),
                        AvgDiscountPercent = $"{Math.Round(g.Average(x => x.Discount) * 100, 1)}%",
                        PreferredOrderMonth = g.GroupBy(x => x.OrderMonth)
                            .OrderByDescending(month => month.Count())
                            .First().Key,
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2)
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .Take(20)
                    .ToArray();

                return 
                    Controls.Stack
                        .WithView(Controls.H2("Customer Purchase Behavior Analysis"))
                        .WithView(layoutArea.ToDataGrid(behaviorData))
                ;
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

        var circles = new List<MapCircle>();
        var validCountries = new List<dynamic>();
        
        // Calculate max customer count for proportional sizing
        var maxCustomerCount = countryData.Max(c => 
            Convert.ToInt32(c.GetType().GetProperty("CustomerCount")?.GetValue(c) ?? 0));
        var minRadius = 40000; // 40km minimum
        var maxRadius = 400000; // 400km maximum
        
        foreach (var country in countryData)
        {
            var countryName = country.GetType().GetProperty("Country")?.GetValue(country)?.ToString() ?? "";
            var customerCount = Convert.ToInt32(country.GetType().GetProperty("CustomerCount")?.GetValue(country) ?? 0);
            var revenue = Convert.ToDouble(country.GetType().GetProperty("TotalRevenue")?.GetValue(country) ?? 0);
            
            if (countryCoordinates.TryGetValue(countryName, out var coordinates))
            {
                validCountries.Add(country);
                
                // Calculate proportional radius based on customer count
                var customerRatio = maxCustomerCount > 0 ? (double)customerCount / maxCustomerCount : 0;
                var radius = minRadius + (maxRadius - minRadius) * customerRatio;
                
                // Color intensity based on customer count (darker blue = more customers)
                var intensity = Math.Min(1.0, customerRatio + 0.3); // Ensure minimum visibility
                var blue = (int)(255 * intensity);
                var fillColor = $"#4444{blue:X2}";
                var strokeColor = $"#0000{blue:X2}";
                
                circles.Add(new MapCircle
                {
                    Center = coordinates,
                    Radius = radius,
                    FillColor = fillColor,
                    FillOpacity = 0.35,
                    StrokeColor = strokeColor,
                    StrokeOpacity = 0.8,
                    StrokeWeight = 2,
                    Id = countryName,
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
            Circles = circles
        }.WithStyle(style => style.WithWidth("100%").WithHeight("500px"));
    }

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}

/// <summary>
/// Represents a toolbar for customer analysis with year filtering.
/// </summary>
public record CustomerToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
