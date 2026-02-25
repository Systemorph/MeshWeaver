// <meshweaver>
// Id: EmployeeNodeViews
// DisplayName: Employee Node Views
// </meshweaver>

using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Charting;
using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Models.Options;
using MeshWeaver.Charting.Pivot;
using MeshWeaver.Layout;
using MeshWeaver.Pivot.Builder;

/// <summary>
/// Instance-level views for individual Employee MeshNodes.
/// Shows sales performance, customer relationships, and order metrics for a single employee.
/// </summary>
public static class EmployeeNodeViews
{
    public static LayoutDefinition AddEmployeeNodeViews(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("SalesPerformance", SalesPerformance)
            .WithView("CustomerBase", CustomerBase)
            .WithView("RecentSales", RecentSales);

    private static int GetEmployeeId(LayoutAreaHost host)
    {
        var content = host.Hub.Configuration.GetContent();
        if (content is JsonElement json && json.TryGetProperty("employeeId", out var eid))
            return eid.GetInt32();
        if (content is EmployeeContent ec)
            return ec.EmployeeId;
        return 0;
    }

    /// <summary>Employee overview with key metrics.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var employeeId = GetEmployeeId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var empData = cubes.Where(c => c.EmployeeId == employeeId).ToList();
                var totalSales = empData.Sum(c => c.Amount);
                var totalOrders = empData.Select(c => c.OrderId).Distinct().Count();
                var customerCount = empData.Select(c => c.Customer).Distinct().Count();
                var avgOrderValue = totalOrders > 0 ? totalSales / totalOrders : 0;

                return (UiControl?)Controls.Stack
                    .WithView(Controls.Markdown($"## Sales Performance"))
                    .WithView(Controls.Html($@"
                        <div style='display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin: 16px 0;'>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>${totalSales:N2}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Total Sales</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{totalOrders}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Orders Handled</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{customerCount}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Customers Served</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>${avgOrderValue:N2}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Avg Order Value</div>
                            </div>
                        </div>
                    "));
            });
    }

    /// <summary>Monthly sales trend for this employee.</summary>
    [Display(GroupName = "Analytics", Order = 0)]
    public static IObservable<UiControl?> SalesPerformance(LayoutAreaHost host, RenderingContext ctx)
    {
        var employeeId = GetEmployeeId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var empData = cubes.Where(c => c.EmployeeId == employeeId).ToList();
                return (UiControl?)empData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderYear))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToBarChart()
                    .WithOptions(o => o.WithTitle("Annual Sales"));
            });
    }

    /// <summary>Top customers served by this employee.</summary>
    [Display(GroupName = "Analytics", Order = 1)]
    public static IObservable<UiControl?> CustomerBase(LayoutAreaHost host, RenderingContext ctx)
    {
        var employeeId = GetEmployeeId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var empData = cubes.Where(c => c.EmployeeId == employeeId).ToList();
                return (UiControl?)empData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceRowsBy(nameof(NorthwindDataCube.CustomerName))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToHorizontalBarChart()
                    .WithOptions(o => o.WithTitle("Top Customers by Revenue"));
            });
    }

    /// <summary>Recent sales handled by this employee.</summary>
    [Display(GroupName = "Sales", Order = 0)]
    public static IObservable<UiControl?> RecentSales(LayoutAreaHost host, RenderingContext ctx)
    {
        var employeeId = GetEmployeeId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var empData = cubes.Where(c => c.EmployeeId == employeeId)
                    .OrderByDescending(c => c.OrderDate)
                    .Take(20)
                    .ToList();
                return (UiControl?)empData.AsQueryable()
                    .ToDataGrid()
                    .WithColumn(c => c.OrderId)
                    .WithColumn(c => c.OrderDate)
                    .WithColumn(c => c.CustomerName)
                    .WithColumn(c => c.ProductName)
                    .WithColumn(c => c.Quantity)
                    .WithColumn(c => c.Amount);
            });
    }
}
