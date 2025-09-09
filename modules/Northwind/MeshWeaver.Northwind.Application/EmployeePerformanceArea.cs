using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.DataCubes;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Pivot.Aggregations;
using MeshWeaver.Pivot.Builder;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates employee performance analytics showing sales achievements, customer relationships, and productivity metrics.
/// Features interactive bar charts ranking employees by revenue and detailed performance tables with key indicators
/// including total sales, order counts, customer coverage, and average order values per employee.
/// </summary>
[Display(GroupName = "Employees", Order = 510)]
public static class EmployeePerformanceArea
{
    /// <summary>
    /// Adds the employee performance area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the employee performance area will be added.</param>
    /// <returns>The updated layout definition with the employee performance area added.</returns>
    public static LayoutDefinition AddEmployeePerformance(this LayoutDefinition layout)
        => layout.WithView(nameof(TopEmployeesByRevenue), TopEmployeesByRevenue)
            .WithView(nameof(EmployeeMetrics), EmployeeMetrics);

    /// <summary>
    /// Displays a horizontal bar chart ranking all employees by total sales revenue.
    /// Shows employee full names (first + last) with corresponding revenue amounts.
    /// Features a year filter toolbar to analyze performance for specific time periods.
    /// Bars are color-coded and automatically sorted from highest to lowest revenue performers.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A horizontal bar chart with employee names and revenue totals, plus year filter controls.</returns>
    public static UiControl? TopEmployeesByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(EmployeeToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new EmployeeToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .CombineLatest(area.Workspace.GetStream<Employee>()!)
                .Select(tuple =>
                {
                    var data = tuple.First;
                    var employees = tuple.Second!.ToDictionary(e => e.EmployeeId, e => $"{e.FirstName} {e.LastName}");
                    
                    var employeeData = data.GroupBy(x => x.Employee)
                        .Select(g => new { 
                            Employee = employees.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(), 
                            Revenue = g.Sum(x => x.Amount) 
                        })
                        .OrderByDescending(x => x.Revenue)
                        .ToArray();

                    var chart = (UiControl)Charting.Chart.Bar(employeeData.Select(e => e.Revenue), "Revenue")
                        .WithLabels(employeeData.Select(e => e.Employee));

                    return Controls.Stack
                        .WithView(Controls.H2("Top Employees by Revenue"))
                        .WithView(chart);
                }));
    }

    /// <summary>
    /// Shows a comprehensive data grid with detailed employee performance metrics.
    /// Displays columns for employee name, total revenue, order count, unique customer count,
    /// and average order value. Rows are sorted by total revenue descending to highlight
    /// top performers. Provides quantitative insights into each employee's sales effectiveness,
    /// customer relationship management, and average deal size.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A data grid table with employee performance metrics and column headers.</returns>
    public static UiControl? EmployeeMetrics(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(EmployeeToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new EmployeeToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
                .CombineLatest(area.Workspace.GetStream<Employee>()!)
                .SelectMany(tuple =>
            {
                var data = tuple.First;
                var employees = tuple.Second!.ToDictionary(e => e.EmployeeId, e => $"{e.FirstName} {e.LastName}");
                
                var employeeMetrics = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = employees.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                        TotalRevenue = Math.Round(g.Sum(x => x.Amount), 2),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count(),
                        CustomerCount = g.Select(x => x.Customer).Distinct().Count(),
                        AvgOrderValue = Math.Round(g.GroupBy(x => x.OrderId).Average(order => order.Sum(x => x.Amount)), 2)
                    })
                    .OrderByDescending(x => x.TotalRevenue);

                return Observable.Return(
                    Controls.Stack
                        .WithView(Controls.H2("Employee Performance Metrics"))
                        .WithView(layoutArea.ToDataGrid(employeeMetrics.ToArray()))
                );
            }));
    }

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData();
}

/// <summary>
/// Represents a toolbar for employee performance analysis with year filtering.
/// </summary>
public record EmployeeToolbar
{
    internal const string Years = "years";
    
    /// <summary>
    /// The year selected in the toolbar.
    /// </summary>
    [Dimension<int>(Options = Years)] public int Year { get; init; }
}