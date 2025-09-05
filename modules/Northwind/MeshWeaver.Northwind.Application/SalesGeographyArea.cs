using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;

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
            .WithView(nameof(RegionalAnalysis), RegionalAnalysis);

    /// <summary>
    /// Gets the country sales comparison chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing country sales comparison.</returns>
    public static IObservable<UiControl> CountrySalesComparison(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .WithAggregation(a => a.Sum(x => x.Amount))
                    .SliceRowsBy(nameof(NorthwindDataCube.ShipCountry))
                    .ToBarChart(builder => builder
)
                    .Select(x => x.ToControl())
            );

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
                        .WithView(Controls.DataGrid(regionalData.ToArray()))
                );
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}