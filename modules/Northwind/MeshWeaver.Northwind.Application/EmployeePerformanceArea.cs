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
/// Provides methods to add and manage employee performance areas in the layout.
/// </summary>
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
    /// Gets the top employees by revenue chart.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing top employees by revenue.</returns>
    public static UiControl? TopEmployeesByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
    {
        layoutArea.SubscribeToDataStream(EmployeeToolbar.Years, layoutArea.GetAllYearsOfOrders());
        return layoutArea.Toolbar(new EmployeeToolbar(), (tb, area, _) =>
            area.GetNorthwindDataCubeData()
                .Select(data => data.Where(x => x.OrderDate >= new DateTime(2023, 1, 1) && (tb.Year == 0 || x.OrderDate.Year == tb.Year)))
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
    /// Gets employee performance metrics.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>An observable sequence of UI controls representing employee metrics.</returns>
    public static IObservable<UiControl> EmployeeMetrics(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetDataCube()
            .CombineLatest(layoutArea.Workspace.GetStream<Employee>()!)
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
            });

    private static IObservable<IEnumerable<NorthwindDataCube>> GetDataCube(this LayoutAreaHost area)
        => area.GetNorthwindDataCubeData()
            .Select(dc => dc.Where(x => x.OrderDate >= new DateTime(2023, 1, 1)));
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