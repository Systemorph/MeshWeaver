using System.Reactive.Linq;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates client performance visualization displaying the top 5 clients by total revenue.
/// Shows a vertical bar chart with customer identifiers and their corresponding sales amounts,
/// featuring data labels positioned for optimal visibility and automatic ranking by performance.
/// </summary>
public static class ClientsOverviewArea
{
    /// <summary>
    /// Adds a clients overview view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout to which the clients overview view will be added.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition AddClientsOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(TopClients), TopClients)
        ;

    /// <summary>
    /// Displays a vertical bar chart showing the top 5 clients ranked by total revenue.
    /// Features customer identifiers as x-axis labels with vertical bars representing their sales amounts.
    /// Data labels are positioned at the start of each bar with end alignment for clear visibility.
    /// Automatically sorts clients from highest to lowest revenue to highlight top performers.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A vertical bar chart control displaying top 5 client revenues with positioned data labels.</returns>
    public static IObservable<UiControl> TopClients(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Customer))
                    .ToBarChart(builder => builder
                        .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        .WithChartBuilder(o => o
                            .WithDataLabels(d =>
                                d.WithAnchor(DataLabelsAnchor.Start)
                                    .WithAlign(DataLabelsAlign.End)
                            )
                        )
                    )
                    .Select(m => m.ToControl())
            );

    /// <summary>
    /// Retrieves the data cube for the specified layout area host.
    /// </summary>
    /// <param name="area">The layout area host.</param>
    /// <returns>An observable sequence of Northwind data cubes.</returns>
    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(1997, 12, 1) && x.OrderDate < new DateTime(2023, 1, 1)));
}
