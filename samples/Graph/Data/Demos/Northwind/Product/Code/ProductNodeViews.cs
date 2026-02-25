// <meshweaver>
// Id: ProductNodeViews
// DisplayName: Product Node Views
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
/// Instance-level views for individual Product MeshNodes.
/// Shows sales history, inventory status, and order details for a single product.
/// </summary>
public static class ProductNodeViews
{
    public static LayoutDefinition AddProductNodeViews(this LayoutDefinition layout) =>
        layout
            .WithView("Overview", Overview)
            .WithView("SalesHistory", SalesHistory)
            .WithView("InventoryStatus", InventoryStatus)
            .WithView("OrderDetails", OrderDetails);

    private static int GetProductId(LayoutAreaHost host)
    {
        var content = host.Hub.Configuration.GetContent();
        if (content is JsonElement json && json.TryGetProperty("productId", out var pid))
            return pid.GetInt32();
        if (content is ProductContent pc)
            return pc.ProductId;
        return 0;
    }

    /// <summary>Product overview with key metrics.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var productId = GetProductId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var productData = cubes.Where(c => c.ProductId == productId).ToList();
                var totalRevenue = productData.Sum(c => c.Amount);
                var totalOrders = productData.Select(c => c.OrderId).Distinct().Count();
                var totalUnits = productData.Sum(c => c.Quantity);
                var avgDiscount = productData.Any() ? productData.Average(c => c.Discount) : 0;

                return (UiControl?)Controls.Stack
                    .WithView(Controls.Markdown($"## Product Sales Summary"))
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
                                <div style='font-size: 24px; font-weight: bold;'>{totalUnits}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Units Sold</div>
                            </div>
                            <div style='padding: 16px; border-radius: 8px; background: var(--mud-palette-surface);'>
                                <div style='font-size: 24px; font-weight: bold;'>{avgDiscount:P1}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Avg Discount</div>
                            </div>
                        </div>
                    "));
            });
    }

    /// <summary>Monthly sales trend for this product.</summary>
    [Display(GroupName = "Analytics", Order = 0)]
    public static IObservable<UiControl?> SalesHistory(LayoutAreaHost host, RenderingContext ctx)
    {
        var productId = GetProductId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var productData = cubes.Where(c => c.ProductId == productId).ToList();
                return (UiControl?)productData.AsQueryable()
                    .ToPivotBuilder()
                    .SliceColumnsBy(nameof(NorthwindDataCube.OrderYear))
                    .WithAggregation(a => a.Sum(nameof(NorthwindDataCube.Amount)))
                    .ToBarChart()
                    .WithOptions(o => o.WithTitle("Annual Sales"));
            });
    }

    /// <summary>Current inventory status.</summary>
    [Display(GroupName = "Inventory", Order = 0)]
    public static IObservable<UiControl?> InventoryStatus(LayoutAreaHost host, RenderingContext ctx)
    {
        var content = host.Hub.Configuration.GetContent();
        if (content is not JsonElement json)
            return Observable.Return<UiControl?>(Controls.Markdown("*No inventory data available*"));

        var unitsInStock = json.TryGetProperty("unitsInStock", out var stock) ? stock.GetInt16() : (short)0;
        var unitsOnOrder = json.TryGetProperty("unitsOnOrder", out var onOrder) ? onOrder.GetInt16() : (short)0;
        var reorderLevel = json.TryGetProperty("reorderLevel", out var reorder) ? reorder.GetInt16() : (short)0;
        var discontinued = json.TryGetProperty("discontinued", out var disc) && disc.GetBoolean();

        var statusColor = discontinued ? "error" : (unitsInStock <= reorderLevel ? "warning" : "success");
        var statusText = discontinued ? "Discontinued" : (unitsInStock <= reorderLevel ? "Low Stock" : "In Stock");

        return Observable.Return<UiControl?>(Controls.Html($@"
            <div style='padding: 16px;'>
                <div style='display: flex; align-items: center; gap: 8px; margin-bottom: 16px;'>
                    <span style='font-size: 14px; padding: 4px 12px; border-radius: 16px; background: var(--mud-palette-{statusColor}); color: white;'>{statusText}</span>
                </div>
                <div style='display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px;'>
                    <div>
                        <div style='font-size: 32px; font-weight: bold;'>{unitsInStock}</div>
                        <div style='color: var(--mud-palette-text-secondary);'>Units in Stock</div>
                    </div>
                    <div>
                        <div style='font-size: 32px; font-weight: bold;'>{unitsOnOrder}</div>
                        <div style='color: var(--mud-palette-text-secondary);'>Units on Order</div>
                    </div>
                    <div>
                        <div style='font-size: 32px; font-weight: bold;'>{reorderLevel}</div>
                        <div style='color: var(--mud-palette-text-secondary);'>Reorder Level</div>
                    </div>
                </div>
            </div>
        "));
    }

    /// <summary>Recent orders containing this product.</summary>
    [Display(GroupName = "Analytics", Order = 1)]
    public static IObservable<UiControl?> OrderDetails(LayoutAreaHost host, RenderingContext ctx)
    {
        var productId = GetProductId(host);
        return host.Workspace.GetObservable<NorthwindDataCube>()
            .Select(cubes =>
            {
                var productData = cubes.Where(c => c.ProductId == productId)
                    .OrderByDescending(c => c.OrderDate)
                    .Take(20)
                    .ToList();
                return (UiControl?)productData.AsQueryable()
                    .ToDataGrid()
                    .WithColumn(c => c.OrderId)
                    .WithColumn(c => c.CustomerName)
                    .WithColumn(c => c.OrderDate)
                    .WithColumn(c => c.Quantity)
                    .WithColumn(c => c.Amount)
                    .WithColumn(c => c.Discount);
            });
    }
}
