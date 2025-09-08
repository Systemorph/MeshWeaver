using System.Reactive.Linq;
using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates employee performance visualization showing the top 5 employees by sales revenue.
/// Displays a horizontal bar chart with employee names and their corresponding sales figures,
/// featuring data labels and automatic sorting from highest to lowest performance.
/// </summary>
public static class EmployeesOverviewArea
{
    /// <summary>
    /// Adds the Employees Overview area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the Employees Overview area will be added.</param>
    /// <returns>The updated layout definition with the Employees Overview area added.</returns>
    public static LayoutDefinition AddEmployeesOverview(this LayoutDefinition layout)
        => layout.WithView(nameof(TopEmployees), TopEmployees);

    /// <summary>
    /// Displays a horizontal bar chart ranking the top 5 employees by total sales revenue.
    /// Shows employee names as labels with horizontal bars extending to represent their sales performance.
    /// Features data labels on each bar showing exact revenue amounts, and bars are automatically
    /// sorted from highest to lowest performer. Uses November 2023 data for the analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A horizontal bar chart control showing top 5 employee performers with data labels.</returns>
    public static IObservable<UiControl> TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
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
                    ).Select(x => x.ToControl())
            );

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 11, 1) && x.OrderDate < new DateTime(2023, 11, 30)));
}
