using System.Reactive.Linq;
using MeshWeaver.GoogleMaps;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Domain;

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
    /// Gets the country sales comparison chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing country sales comparison.</returns>
    public static IObservable<UiControl> CountrySalesComparison(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
            {
                var countryData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new { Country = g.Key!, Revenue = g.Sum(x => x.Amount) })
                    .OrderByDescending(x => x.Revenue)
                    .Take(15)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Bar(countryData.Select(c => c.Revenue), "Revenue")
                    .WithLabels(countryData.Select(c => c.Country));

                return Controls.Stack
                    .WithView(Controls.H2("Sales by Country"))
                    .WithView(chart);
            });

    /// <summary>
    /// Gets the regional analysis with data grid.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing regional analysis.</returns>
    public static IObservable<UiControl> RegionalAnalysis(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
            {
                var regionalData = data.Where(x => !string.IsNullOrEmpty(x.ShipCountry))
                    .GroupBy(x => x.ShipCountry)
                    .Select(g => new
                    {
                        Country = g.Key!,
                        TotalRevenue = g.Sum(x => x.Amount),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Regional Sales Analysis"))
                        .WithView(layoutArea.ToDataGrid(regionalData.ToArray()))
                );
            });

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
    /// Gets the sales map view with toolbar to toggle between map and table.
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
                        TotalRevenue = g.Sum(x => x.Amount),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Global Sales Distribution"))
                        .WithView(layoutArea.ToDataGrid(countryData))
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
                    .Select(g => new
                    {
                        Country = g.Key!,
                        TotalRevenue = g.Sum(x => x.Amount),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.TotalRevenue)
                    .ToArray();

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Global Sales Distribution"))
                        .WithView(CreateSalesMap(countryData))
                );
            });

    private static UiControl CreateSalesMap(object[] countryData)
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
            var revenue = country.GetType().GetProperty("TotalRevenue")?.GetValue(country) ?? 0;
            
            if (countryCoordinates.TryGetValue(countryName, out var coordinates))
            {
                validCountries.Add(country);
                markers.Add(new MapMarker
                {
                    Position = coordinates,
                    Title = $"{countryName}: ${revenue:N0}",
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
