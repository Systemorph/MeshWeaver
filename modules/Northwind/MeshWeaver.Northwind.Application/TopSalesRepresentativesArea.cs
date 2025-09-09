using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Northwind.Domain;
using MeshWeaver.Domain;

namespace MeshWeaver.Northwind.Application;

/// <summary>
/// Creates comprehensive sales representatives performance analysis with both visual charts and tabular data.
/// Features interactive bar charts showing top employees by revenue and dynamically generated markdown tables
/// with detailed earnings breakdowns, employee insights, and performance recommendations.
/// </summary>
public static class TopSalesRepresentativesArea
{
    /// <summary>
    /// Adds the top sales representatives area to the layout.
    /// </summary>
    /// <param name="layout">The layout definition to which the top sales representatives area will be added.</param>
    /// <returns>The updated layout definition with the top sales representatives area added.</returns>
    public static LayoutDefinition AddTopSalesRepresentatives(this LayoutDefinition layout)
        => layout.WithView(nameof(TopEmployees), TopEmployees)
            .WithView(nameof(TopEmployeesTable), TopEmployeesTable)
            .WithView(nameof(TopEmployeesReport), TopEmployeesReport);

    /// <summary>
    /// Displays a horizontal bar chart ranking employees by total sales revenue.
    /// Shows employee full names (first + last) with corresponding revenue amounts in a clean,
    /// color-coded bar chart. Automatically filters to show top performers and sorts from
    /// highest to lowest earnings for easy performance comparison.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A horizontal bar chart with employee names and revenue totals.</returns>
    public static IObservable<UiControl> TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Employee>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var employees = tuple.Second!.ToDictionary(e => e.EmployeeId, e => $"{e.FirstName} {e.LastName}");

                var employeeData = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = employees.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                        Revenue = g.Sum(x => x.Amount)
                    })
                    .OrderByDescending(x => x.Revenue)
                    .Take(8)
                    .ToArray();

                var chart = (UiControl)Charting.Chart.Bar(employeeData.Select(e => e.Revenue), "Revenue")
                    .WithLabels(employeeData.Select(e => e.Employee));

                return Controls.Stack
                    .WithView(Controls.H2("Top Sales Representatives"))
                    .WithView(chart);
            });

    /// <summary>
    /// Generates a dynamic markdown table showing detailed employee earnings breakdown.
    /// Creates a properly formatted markdown table with employee names and their monthly amounts earned,
    /// using real data from the system. The table includes proper escaping for dollar signs and
    /// currency formatting for professional presentation.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A markdown-formatted table with employee earnings data.</returns>
    public static IObservable<UiControl> TopEmployeesTable(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Employee>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var employees = tuple.Second!.ToDictionary(e => e.EmployeeId, e => $"{e.FirstName} {e.LastName}");

                var employeeData = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = employees.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                        Revenue = g.Sum(x => x.Amount)
                    })
                    .OrderByDescending(x => x.Revenue)
                    .Take(5)
                    .ToArray();

                var markdownTable = new StringBuilder();
                markdownTable.AppendLine("| Employee Name     | Amount Earned (Monthly) |");
                markdownTable.AppendLine("|-------------------|-------------------------|");

                foreach (var emp in employeeData)
                {
                    markdownTable.AppendLine($"| {emp.Employee.PadRight(17)} | \\${emp.Revenue:N2}              |");
                }

                return Controls.Markdown(markdownTable.ToString());
            });

    /// <summary>
    /// Creates a concise performance report with key metrics and top performer recognition.
    /// Generates essential performance data and recognition for top employees based on sales data.
    /// </summary>
    /// <param name="layoutArea">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>A concise markdown report with key performance insights.</returns>
    public static IObservable<UiControl> TopEmployeesReport(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.GetNorthwindDataCubeData()
            .CombineLatest(layoutArea.Workspace.GetStream<Employee>()!)
            .Select(tuple =>
            {
                var data = tuple.First;
                var employees = tuple.Second!.ToDictionary(e => e.EmployeeId, e => $"{e.FirstName} {e.LastName}");

                var employeeData = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = employees.TryGetValue(g.Key, out var name) ? name : g.Key.ToString(),
                        Revenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.Revenue)
                    .ToArray();

                var topEmployee = employeeData.FirstOrDefault();
                var secondEmployee = employeeData.Skip(1).FirstOrDefault();
                var totalRevenue = employeeData.Sum(x => x.Revenue);
                var avgRevenue = employeeData.Average(x => x.Revenue);

                var report = new StringBuilder();
                report.AppendLine("## Key Performance Insights:");

                if (topEmployee != null)
                {
                    report.AppendLine($"🏆 **{topEmployee.Employee}** is the top performer with **\\${topEmployee.Revenue:N2}** in sales");
                }

                if (secondEmployee != null)
                {
                    report.AppendLine($"🥈 **{secondEmployee.Employee}** follows with **\\${secondEmployee.Revenue:N2}**");
                }

                report.AppendLine();
                report.AppendLine($"📊 **Total Team Revenue:** \\${totalRevenue:N2}");
                report.AppendLine($"📈 **Average Revenue per Employee:** \\${avgRevenue:N2}");

                return Controls.Markdown(report.ToString());
            });
}