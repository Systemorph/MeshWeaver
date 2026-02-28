// <meshweaver>
// Id: ProductNodeViews
// DisplayName: Product Node Views
// </meshweaver>

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Product MeshNodes.
/// Displays product information, pricing, and inventory status.
/// </summary>
public static class ProductNodeViews
{
    public static LayoutDefinition AddProductNodeViews(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("ProductOverview")
            .WithView("ProductOverview", ProductOverview)
            .WithView("InventoryStatus", InventoryStatus);

    private static ProductContent? ExtractProductContent(MeshNode? node)
    {
        if (node?.Content == null)
            return null;

        if (node.Content is ProductContent pc)
            return pc;

        if (node.Content is JsonElement json)
        {
            return new ProductContent
            {
                ProductId = json.TryGetProperty("productId", out var pid) ? pid.GetInt32() : 0,
                ProductName = json.TryGetProperty("productName", out var pn) ? pn.GetString() ?? "" : "",
                SupplierId = json.TryGetProperty("supplierId", out var sid) ? sid.GetInt32() : 0,
                CategoryId = json.TryGetProperty("categoryId", out var cid) ? cid.GetInt32() : 0,
                QuantityPerUnit = json.TryGetProperty("quantityPerUnit", out var qpu) ? qpu.GetString() ?? "" : "",
                UnitPrice = json.TryGetProperty("unitPrice", out var up) ? up.GetDouble() : 0,
                UnitsInStock = json.TryGetProperty("unitsInStock", out var uis) ? uis.GetInt16() : (short)0,
                UnitsOnOrder = json.TryGetProperty("unitsOnOrder", out var uoo) ? uoo.GetInt16() : (short)0,
                ReorderLevel = json.TryGetProperty("reorderLevel", out var rl) ? rl.GetInt16() : (short)0,
                Discontinued = json.TryGetProperty("discontinued", out var disc) && disc.GetBoolean()
            };
        }
        return null;
    }

    /// <summary>Product overview with details.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> ProductOverview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var product = ExtractProductContent(node);

            if (product == null)
                return (UiControl?)Controls.Markdown("*Product data not available*");

            var statusBadge = product.Discontinued
                ? "<span style='padding: 4px 12px; border-radius: 16px; background: var(--mud-palette-error); color: white; font-size: 12px;'>Discontinued</span>"
                : "<span style='padding: 4px 12px; border-radius: 16px; background: var(--mud-palette-success); color: white; font-size: 12px;'>Active</span>";

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown($"## {product.ProductName}"))
                .WithView(Controls.Html($@"
                    <div style='margin-bottom: 16px;'>{statusBadge}</div>
                    <div style='display: grid; grid-template-columns: repeat(2, 1fr); gap: 24px; margin: 16px 0;'>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Product Details</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Product ID</div>
                                <div style='font-size: 16px; font-weight: 500;'>{product.ProductId}</div>
                            </div>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Quantity Per Unit</div>
                                <div style='font-size: 16px;'>{(string.IsNullOrWhiteSpace(product.QuantityPerUnit) ? "—" : product.QuantityPerUnit)}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Category ID</div>
                                <div style='font-size: 16px;'>{product.CategoryId}</div>
                            </div>
                        </div>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Pricing</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Unit Price</div>
                                <div style='font-size: 24px; font-weight: bold; color: var(--mud-palette-primary);'>${product.UnitPrice:N2}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Supplier ID</div>
                                <div style='font-size: 16px;'>{product.SupplierId}</div>
                            </div>
                        </div>
                    </div>
                "));
        });
    }

    /// <summary>Product inventory status.</summary>
    [Display(GroupName = "Inventory", Order = 0)]
    public static IObservable<UiControl?> InventoryStatus(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var product = ExtractProductContent(node);

            if (product == null)
                return (UiControl?)Controls.Markdown("*Product data not available*");

            var stockStatus = product.Discontinued ? "Discontinued"
                : product.UnitsInStock <= product.ReorderLevel ? "Low Stock"
                : "In Stock";
            var statusColor = product.Discontinued ? "error"
                : product.UnitsInStock <= product.ReorderLevel ? "warning"
                : "success";

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown("## Inventory Status"))
                .WithView(Controls.Html($@"
                    <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                        <div style='margin-bottom: 20px;'>
                            <span style='padding: 6px 16px; border-radius: 20px; background: var(--mud-palette-{statusColor}); color: white; font-size: 14px;'>{stockStatus}</span>
                        </div>
                        <div style='display: grid; grid-template-columns: repeat(3, 1fr); gap: 24px;'>
                            <div>
                                <div style='font-size: 36px; font-weight: bold;'>{product.UnitsInStock}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Units in Stock</div>
                            </div>
                            <div>
                                <div style='font-size: 36px; font-weight: bold;'>{product.UnitsOnOrder}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Units on Order</div>
                            </div>
                            <div>
                                <div style='font-size: 36px; font-weight: bold;'>{product.ReorderLevel}</div>
                                <div style='color: var(--mud-palette-text-secondary);'>Reorder Level</div>
                            </div>
                        </div>
                    </div>
                "));
        });
    }
}
