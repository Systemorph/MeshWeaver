using System.Reactive.Linq;
using MeshWeaver.Charting;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Domain;
using MeshWeaver.Charting.Models.Bar;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add and manage sales geography areas in the layout.
/// </summary>
public static class SalesGeographyArea
{
    /// <summary>
    /// Adds the sales geography area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the sales geography area will be added.</param>
    /// <returns>The updated layout definition with the sales geography area added.</returns>
    public static LayoutDefinition AddSalesGeography(this LayoutDefinition layout)
        => layout.WithView(nameof(CountrySalesComparison), CountrySalesComparison)
            .WithView(nameof(RegionalAnalysis), RegionalAnalysis)
            .WithView(nameof(SalesMapView), SalesMapView);

    /// <summary>
    /// Shows a stacked bar chart with sales by country. Can be filtered by year.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing country sales comparison.</returns>
    public static UiControl? CountrySalesComparison(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(SalesGeographyToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new SalesGeographyToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .SelectMany(data =>
                {
                    var filteredData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry));
                    
                    // Group by country and year to create chart datasets
                    var countryYearGroups = filteredData
                        .GroupBy(x => new { x.ShipCountry, x.OrderYear })
                        .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
                    
                    // Get unique years and countries
                    var years = countryYearGroups.Keys.Select(k => k.OrderYear).Distinct().OrderBy(y => y).ToArray();
                    var countries = countryYearGroups.Keys.Select(k => k.ShipCountry).Distinct().OrderBy(c => c).ToArray();
                    
                    // Create a dataset for each year
                    var dataSets = years.Select(year =>
                    {
                        var yearData = countries.Select(country => 
                            countryYearGroups.TryGetValue(new { ShipCountry = country, OrderYear = year }, out var amount) ? amount : 0.0)
                            .ToArray();
                        return new BarDataSet(yearData).WithLabel(year.ToString());
                    }).ToArray();
                    
                    var chart = Chart.Create(dataSets)
                        .Stacked()
                        .WithLabels(countries);
                    
                    return Observable.Return((UiControl)Controls.Stack
                        .WithView(Controls.H2("Sales by Country (Stacked by Year)"))
                        .WithView(chart.ToControl()));
                }));
    }

    /// <summary>
    /// Shows a data grid with regional sales analysis including total revenue, customer count, and order count by country. Can be filtered by year.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing regional analysis.</returns>
    public static UiControl RegionalAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(SalesGeographyToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new SalesGeographyToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .SelectMany(data =>
                {
                    var regionalData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                        .GroupBy(x => x.ShipCountry)
                        .Select(g => new
                        {
                            Country = g.Key!,
                            TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                            CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                            OrderCount = g.DistinctBy(x => x.OrderId).Count()
                        })
                        .OrderByDescending(x => x.TotalRevenue);

                    return Observable.Return(
                        Controls.Stack
                            .WithView(Controls.H2("Regional Sales Analysis"))
                            .WithView(layoutArea.ToDataGrid(regionalData.ToArray()))
                    );
                }));
    }

    /// <summary>
    /// Toolbar configuration for sales map view.
    /// </summary>
    private record SalesMapToolbar
    {
        public const string Map = nameof(Map);
        public const string Table = nameof(Table);

        [UiControl<RadioGroupControl>(Options = new[] { "Map", "Table" })]
        public string Display { get; init; } = Map;
    }

    /// <summary>
    /// Shows an interactive Google Maps view with sales data represented as circles, with toolbar to toggle between map and data grid table.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing sales map with toolbar.</returns>
    public static UiControl? SalesMapView(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        return layoutArea.Toolbar(new SalesMapToolbar(),
            (toolbar, area, _) => toolbar.Display switch
            {
                SalesMapToolbar.Table => area.SalesMapTable(),
                _ => area.SalesMapChart()
            }
        );
    }

    /// <summary>
    /// Gets the table view for sales map data.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing sales data table.</returns>
    private static IObservable<UiControl> SalesMapTable(this LayoutAreaHost layoutArea)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var countryData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new
                    {
                        Country = g.Key!,
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Global Sales Distribution"))
                        .WithView(layoutArea.ToDataGrid(countryData))
                        .WithClass("full-width-container")
                );
            });

    /// <summary>
    /// Gets the map view for sales data.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <returns>An observable sequence of UI controls representing sales map.</returns>
    private static IObservable<UiControl> SalesMapChart(this LayoutAreaHost layoutArea)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var countryData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new CountryData
                    {
                        Country = g.Key!,
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Global Sales Distribution"))
                        .WithView(
                            CreateSalesMapWithDetails(layoutArea, countryData)
                        )
                        .WithStyle(style => style.WithWidth("100%"))
                );
            });

    private record CountryData
    {
        public required string Country { get; init; }
        public required double TotalRevenue { get; init; }
        public required int CustomerCount { get; init; }
        public required int OrderCount { get; init; }
    }
    private static UiControl CreateSalesMapWithDetails(LayoutAreaHost host, CountryData[] countryData)
    {
        const string SalesDetails = nameof(SalesDetails);
        return Controls.Stack
            .WithWidth("100%")
            .WithView(CreateSalesMapControl(countryData)
            .WithClickAction(context =>
            {
                var selectedCountryId = context.Payload?.ToString();
                if (selectedCountryId == null)
                    return;
                var selectedCountry = countryData.FirstOrDefault(cd => cd.Country == selectedCountryId);
                if (selectedCountry is null)
                    return;
                // Replace last segment of context.Area with "SalesDetail"
                var areaSegments = context.Area.Split('/');
                if (areaSegments.Length > 0)
                    areaSegments[^1] = SalesDetails;
                var salesDetailArea = string.Join('/', areaSegments);
                host.UpdateArea(salesDetailArea, 
                    Controls.Markdown($"""
                                      ## {selectedCountry.Country} Sales Details
                                      
                                      | Metric | Value |
                                      |--------|-------|
                                      | Total Revenue | ${selectedCountry.TotalRevenue:N2} |
                                      | Customer Count | {selectedCountry.CustomerCount:N0} |
                                      | Order Count | {selectedCountry.OrderCount:N0} |
                                      | Average Revenue per Customer | ${selectedCountry.TotalRevenue / Math.Max(selectedCountry.CustomerCount, 1):N2} |
                                      | Average Revenue per Order | ${selectedCountry.TotalRevenue / Math.Max(selectedCountry.OrderCount, 1):N2} |
                                      """));
            }))
            .WithView(Controls.H4("Click circle for details"), SalesDetails);
    }

    private static UiControl CreateSalesMapControl(CountryData[] countryData)
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
        
        // Calculate max revenue for proportional sizing
        var maxRevenue = countryData.Max(c => c.TotalRevenue);
        var minRadius = 50000; // 50km minimum
        var maxRadius = 500000; // 500km maximum
        
        foreach (var country in countryData)
        {
            if (countryCoordinates.TryGetValue(country.Country, out var coordinates))
            {
                // Calculate proportional radius based on revenue
                var revenueRatio = country.TotalRevenue / maxRevenue;
                var radius = minRadius + (maxRadius - minRadius) * revenueRatio;
                
                // Color intensity based on revenue (darker = higher revenue)
                var intensity = Math.Min(1.0, revenueRatio + 0.3); // Ensure minimum visibility
                var red = (int)(255 * intensity);
                var fillColor = $"#{red:X2}4444";
                var strokeColor = $"#{red:X2}0000";
                
                circles.Add(new MapCircle
                {
                    Center = coordinates,
                    Radius = radius,
                    FillColor = fillColor,
                    FillOpacity = 0.35,
                    StrokeColor = strokeColor,
                    StrokeOpacity = 0.8,
                    StrokeWeight = 2,
                    Id = country.Country,
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
        }.WithClass("full-width-map")
         .WithStyle(style => style.WithWidth("100%").WithHeight("600px"));
    }

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}

/// <summary>
/// Represents a toolbar for sales geography analysis with year filtering.
/// </summary>
public record SalesGeographyToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}
