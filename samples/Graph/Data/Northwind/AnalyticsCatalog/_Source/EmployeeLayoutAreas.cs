// <meshweaver>
// Id: EmployeeLayoutAreas
// DisplayName: Employee Views
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;

/// <summary>
/// Employee performance views.
/// </summary>
[Display(GroupName = "Employees", Order = 300)]
public static class EmployeeLayoutAreas
{
    public static LayoutDefinition AddEmployeeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithView(nameof(EmployeeMetrics), EmployeeMetrics)
            .WithView(nameof(TopEmployees), TopEmployees)
            .WithView(nameof(TopEmployeesTable), TopEmployeesTable)
            .WithView(nameof(TopEmployeesReport), TopEmployeesReport)
            .WithView(nameof(TopEmployeesByRevenue), TopEmployeesByRevenue);

    /// <summary>
    /// Employee performance metrics as a bar chart.
    /// </summary>
    [Display(GroupName = "Employees", Order = 300)]
    public static UiControl EmployeeMetrics(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var byEmployee = data
                .GroupBy(x => x.EmployeeName ?? x.Employee.ToString())
                .Select(g => new { Employee = g.Key, Revenue = g.Sum(x => x.Amount), Orders = g.DistinctBy(x => x.OrderId).Count() })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            return (UiControl)Charts.Bar(
                byEmployee.Select(x => x.Revenue),
                byEmployee.Select(x => x.Employee)
            ).WithTitle($"Employee Revenue Performance ({year})");
        });

    /// <summary>
    /// Top employees by revenue column chart.
    /// </summary>
    [Display(GroupName = "Employees", Order = 301)]
    public static UiControl TopEmployees(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var topEmployees = data
                .GroupBy(x => x.EmployeeName ?? x.Employee.ToString())
                .Select(g => new { Employee = g.Key, Revenue = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Revenue)
                .Take(5)
                .ToArray();

            return (UiControl)Charts.Column(
                topEmployees.Select(x => x.Revenue),
                topEmployees.Select(x => x.Employee)
            ).WithTitle($"Top 5 Employees by Revenue ({year})");
        });

    /// <summary>
    /// Employee earnings table in markdown (all years).
    /// </summary>
    [Display(GroupName = "Employees", Order = 302)]
    public static IObservable<UiControl> TopEmployeesTable(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var employeeData = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = g.First().EmployeeName ?? g.Key.ToString(),
                        Revenue = g.Sum(x => x.Amount)
                    })
                    .OrderByDescending(x => x.Revenue)
                    .Take(5)
                    .ToArray();

                var markdownTable = new StringBuilder();
                markdownTable.AppendLine("| Employee Name     | Total Revenue |");
                markdownTable.AppendLine("|-------------------|---------------|");

                foreach (var emp in employeeData)
                    markdownTable.AppendLine($"| {emp.Employee,-17} | \\${emp.Revenue:N2} |");

                return (UiControl)Controls.Markdown(markdownTable.ToString());
            });

    /// <summary>
    /// Employee performance report with insights (all years).
    /// </summary>
    [Display(GroupName = "Employees", Order = 303)]
    public static IObservable<UiControl> TopEmployeesReport(this LayoutAreaHost layoutArea, RenderingContext context) =>
        layoutArea.GetNorthwindDataCubeData()
            .Select(data =>
            {
                var employeeData = data.GroupBy(x => x.Employee)
                    .Select(g => new
                    {
                        Employee = g.First().EmployeeName ?? g.Key.ToString(),
                        Revenue = g.Sum(x => x.Amount),
                        OrderCount = g.DistinctBy(x => x.OrderId).Count()
                    })
                    .OrderByDescending(x => x.Revenue)
                    .ToArray();

                var topEmployee = employeeData.FirstOrDefault();
                var totalRevenue = employeeData.Sum(x => x.Revenue);
                var avgRevenue = employeeData.Length > 0 ? employeeData.Average(x => x.Revenue) : 0;

                var report = new StringBuilder();
                report.AppendLine("## Key Performance Insights");
                if (topEmployee != null)
                    report.AppendLine($"**{topEmployee.Employee}** is the top performer with **\\${topEmployee.Revenue:N2}** in sales");
                report.AppendLine();
                report.AppendLine($"**Total Team Revenue:** \\${totalRevenue:N2}");
                report.AppendLine($"**Average Revenue per Employee:** \\${avgRevenue:N2}");

                return (UiControl)Controls.Markdown(report.ToString());
            });

    /// <summary>
    /// All employees ranked by revenue with detailed metrics.
    /// </summary>
    [Display(GroupName = "Employees", Order = 304)]
    public static UiControl TopEmployeesByRevenue(this LayoutAreaHost layoutArea, RenderingContext context)
        => layoutArea.WithYearToolbar((year, data) =>
        {
            var employeeData = data
                .GroupBy(x => x.Employee)
                .Select(g => new
                {
                    Employee = g.First().EmployeeName ?? g.Key.ToString(),
                    Revenue = g.Sum(x => x.Amount),
                    Orders = g.DistinctBy(x => x.OrderId).Count(),
                    Customers = g.Select(x => x.Customer).Distinct().Count()
                })
                .OrderByDescending(x => x.Revenue)
                .ToArray();

            return (UiControl)Controls.Stack
                .WithView(Controls.H2($"Employee Performance Metrics ({year})"))
                .WithView(layoutArea.ToDataGrid(employeeData, config => config.AutoMapProperties()));
        });
}
