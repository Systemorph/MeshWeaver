using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Pivot.Builder;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates employee performance visualization showing the top 5 employees by sales revenue.
/// Displays a horizontal bar chart with employee names and their corresponding sales figures,
/// featuring data labels and automatic sorting from highest to lowest performance.
/// </summary>
[Display(GroupName = "Employees", Order = 500)]
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
    /// sorted from highest to lowest performer. Uses November 2025 data for the analysis.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A horizontal bar chart control showing top 5 employee performers with data labels.</returns>
    public static IObservable<UiControl> TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .Select(data =>
            {
                var topEmployees = data
                    .GroupBy(x => x.EmployeeName ?? x.Employee.ToString())
                    .Select(g => new { EmployeeName = g.Key, Revenue = g.Sum(x => x.Amount) })
                    .OrderByDescending(x => x.Revenue)
                    .Take(5)
                    .ToArray();

                return (UiControl)Charts.Column(
                    topEmployees.Select(x => x.Revenue),
                    topEmployees.Select(x => x.EmployeeName)
                ).WithTitle("Top 5 Employees");
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2025, 11, 1) && x.OrderDate < new DateTime(2025, 11, 30)));
}
