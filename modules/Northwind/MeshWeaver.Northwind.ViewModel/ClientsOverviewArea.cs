using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Data;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides extension methods for adding client overview functionality to a layout.
/// </summary>
public static class ClientsOverviewArea
{
    /// <summary>
    /// Adds a clients overview view to the specified layout.
    /// </summary>
    /// <param name="layout">The layout to which the clients overview view will be added.</param>
    /// <returns>The updated layout definition.</returns>
    public static LayoutDefinition AddClientsOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(TopClients), Controls.Stack.WithView(TopClients))
            .WithNavMenu((menu, _, _) =>
                menu.WithNavLink(
                    nameof(TopClients),
                    new LayoutAreaReference(nameof(TopClients)).ToHref(layout.Hub.Address), FluentIcons.Document)
            )
        ;

    /// <summary>
    /// Generates a bar chart of the top clients for the specified layout area and rendering context.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of objects representing the top clients bar chart.</returns>
    public static IObservable<object> TopClients(this LayoutAreaHost layoutArea, RenderingContext context)
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
