using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.ViewModel;

/// <summary>
/// Provides methods to add and manage the Employees Overview area in the layout.
/// </summary>
public static class EmployeesOverviewArea
{
    /// <summary>
    /// Adds the Employees Overview area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the Employees Overview area will be added.</param>
    /// <returns>The updated layout definition with the Employees Overview area added.</returns>
    public static LayoutDefinition AddEmployeesOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(TopEmployees), Controls.Stack.WithView(TopEmployees));

    /// <summary>
    /// Generates a bar chart of the top 5 employees based on the provided data cube.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence containing the bar chart object.</returns>
    public static IObservable<object> TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .SelectMany(data =>
                layoutArea.Workspace
                    .Pivot(data.ToDataCube())
                    .SliceColumnsBy(nameof(Employee))
                    .ToBarChart(builder => builder
                        .WithOptions(o => o.OrderByValueDescending().TopValues(5))
                        .WithChartBuilder(o => o
                            .AsHorizontal()
                            .WithDataLabels()
                        )
                    )
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 11, 1) && x.OrderDate < new DateTime(2023, 11, 30)));
}
