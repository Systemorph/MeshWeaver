// <meshweaver>
// Id: CustomerNodeViews
// DisplayName: Customer Node Views
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
/// Instance-level views for individual Customer MeshNodes.
/// Shows order history, spending analysis, and purchase patterns for a single customer.
/// </summary>
public static class CustomerNodeViews
{
    public static LayoutDefinition AddCustomerNodeViews(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("OrderHistory", OrderHistory)
            .WithView("ProductPreferences", ProductPreferences)
            .WithView("RecentOrders", RecentOrders);

    private static string GetCustomerId(LayoutAreaHost host)
    {
        var content = host.Hub.Configuration.GetContent();
        if (content is JsonElement json && json.TryGetProperty("customerId", out var cid))
            return cid.GetString() ?? "";
        if (content is CustomerContent cc)
            return cc.CustomerId;
        return "";
    }

    /// <summary>Customer overview with key metrics.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var customerId = GetCustomerId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var customerData = cubes.Where(c => c.Customer == customerId).ToList();
                var totalSpending = customerData.Sum(c => c.Amount);
                var totalOrders = customerData.Select(c => c.OrderId).Distinct().Count();
                var avgOrderValue = totalOrders > 0 ? totalSpending / totalOrders : 0;
                var productCount = customerData.Select(c => c.ProductId).Distinct().Count();

                return (UiControl?)Controls.Stack
                    .WithView(Controls.Markdown($"## Customer Analytics"))
                    .WithView(Controls.Html($@"
                        <div style='display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin: 16px 0;'>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>${totalSpending:N2}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Total Spending</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{totalOrders}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Orders</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>${avgOrderValue:N2}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Avg Order Value</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{productCount}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Products Ordered</div>
                            </div>
                        </div>
                    "));
            });
    }

    /// <summary>Monthly spending trend for this customer.</summary>
    [Display(GroupName = "Analytics", Order = 0)]
    public static IObservable<UiControl?> OrderHistory(LayoutAreaHost host, RenderingContext ctx)
    {
        var customerId = GetCustomerId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var customerData = cubes.Where(c => c.Customer == customerId).ToList();
                return (UiControl?)customerData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderYear))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToBarChart()
                    .WithOptions(o => o.WithTitle("Annual Spending"));
            });
    }

    /// <summary>Top products purchased by this customer.</summary>
    [Display(GroupName = "Analytics", Order = 1)]
    public static IObservable<UiControl?> ProductPreferences(LayoutAreaHost host, RenderingContext ctx)
    {
        var customerId = GetCustomerId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var customerData = cubes.Where(c => c.Customer == customerId).ToList();
                return (UiControl?)customerData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceRowsBy(nameof(NorthwindDataCube.ProductName))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToHorizontalBarChart()
                    .WithOptions(o => o.WithTitle("Top Products by Revenue"));
            });
    }

    /// <summary>Recent order details.</summary>
    [Display(GroupName = "Orders", Order = 0)]
    public static IObservable<UiControl?> RecentOrders(LayoutAreaHost host, RenderingContext ctx)
    {
        var customerId = GetCustomerId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var customerData = cubes.Where(c => c.Customer == customerId)
                    .OrderByDescending(c => c.OrderDate)
                    .Take(20)
                    .ToList();
                return (UiControl?)customerData.AsQueryable()
                    .ToDataGrid()
                    .WithColumn(c => c.OrderId)
                    .WithColumn(c => c.OrderDate)
                    .WithColumn(c => c.ProductName)
                    .WithColumn(c => c.Quantity)
                    .WithColumn(c => c.Amount)
                    .WithColumn(c => c.EmployeeName);
            });
    }
}
