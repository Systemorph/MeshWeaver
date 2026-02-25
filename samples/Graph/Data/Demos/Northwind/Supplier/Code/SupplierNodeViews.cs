// <meshweaver>
// Id: SupplierNodeViews
// DisplayName: Supplier Node Views
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
/// Instance-level views for individual Supplier MeshNodes.
/// Shows product catalog, sales performance, and order metrics for a single supplier.
/// </summary>
public static class SupplierNodeViews
{
    public static LayoutDefinition AddSupplierNodeViews(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("SalesPerformance", SalesPerformance)
            .WithView("ProductCatalog", ProductCatalog)
            .WithView("RecentOrders", RecentOrders);

    private static int GetSupplierId(LayoutAreaHost host)
    {
        var content = host.Hub.Configuration.GetContent();
        if (content is JsonElement json && json.TryGetProperty("supplierId", out var sid))
            return sid.GetInt32();
        if (content is SupplierContent sc)
            return sc.SupplierId;
        return 0;
    }

    /// <summary>Supplier overview with key metrics.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var supplierId = GetSupplierId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var supplierData = cubes.Where(c => c.SupplierId == supplierId).ToList();
                var totalRevenue = supplierData.Sum(c => c.Amount);
                var totalOrders = supplierData.Select(c => c.OrderId).Distinct().Count();
                var productCount = supplierData.Select(c => c.ProductId).Distinct().Count();
                var totalUnits = supplierData.Sum(c => c.Quantity);

                return (UiControl?)Controls.Stack
                    .WithView(Controls.Markdown($"## Supplier Performance"))
                    .WithView(Controls.Html($@"
                        <div style='display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; margin: 16px 0;'>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>${totalRevenue:N2}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Total Revenue</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{totalOrders}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Orders</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{productCount}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Products</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{totalUnits}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Units Sold</div>
                            </div>
                        </div>
                    "));
            });
    }

    /// <summary>Monthly sales trend for products from this supplier.</summary>
    [Display(GroupName = "Analytics", Order = 0)]
    public static IObservable<UiControl?> SalesPerformance(LayoutAreaHost host, RenderingContext ctx)
    {
        var supplierId = GetSupplierId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var supplierData = cubes.Where(c => c.SupplierId == supplierId).ToList();
                return (UiControl?)supplierData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderYear))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToBarChart()
                    .WithOptions(o => o.WithTitle("Annual Sales"));
            });
    }

    /// <summary>Products supplied by this supplier with sales breakdown.</summary>
    [Display(GroupName = "Analytics", Order = 1)]
    public static IObservable<UiControl?> ProductCatalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var supplierId = GetSupplierId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var supplierData = cubes.Where(c => c.SupplierId == supplierId).ToList();
                return (UiControl?)supplierData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceRowsBy(nameof(NorthwindDataCube.ProductName))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToHorizontalBarChart()
                    .WithOptions(o => o.WithTitle("Products by Revenue"));
            });
    }

    /// <summary>Recent orders for products from this supplier.</summary>
    [Display(GroupName = "Orders", Order = 0)]
    public static IObservable<UiControl?> RecentOrders(LayoutAreaHost host, RenderingContext ctx)
    {
        var supplierId = GetSupplierId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var supplierData = cubes.Where(c => c.SupplierId == supplierId)
                    .OrderByDescending(c => c.OrderDate)
                    .Take(20)
                    .ToList();
                return (UiControl?)supplierData.AsQueryable()
                    .ToDataGrid()
                    .WithColumn(c => c.OrderId)
                    .WithColumn(c => c.OrderDate)
                    .WithColumn(c => c.ProductName)
                    .WithColumn(c => c.CustomerName)
                    .WithColumn(c => c.Quantity)
                    .WithColumn(c => c.Amount);
            });
    }
}
