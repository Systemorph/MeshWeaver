using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Provides methods to add revenue summary to the layout and retrieve revenue data.
/// </summary>
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
    /// Retrieves the revenue summary data and converts it to a line chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the revenue summary line chart.</returns>
    public static IObservable<UiControl> RevenueSummary(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                    .ToLineChart(builder => builder).Select(x => x.ToControl())

            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(d => d.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
}
