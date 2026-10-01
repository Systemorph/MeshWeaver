// <meshweaver>
// Id: ProductNodeLayoutAreas
// DisplayName: Product Node Views
// </meshweaver>

using MeshWeaver.Domain;
using MeshWeaver.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Instance-level views for individual Product MeshNodes.
///
/// <para>Both views are TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"). Stored
/// fields are <see cref="JsonPointerReference"/>s into the node's <see cref="ProductContent"/>,
/// resolved by the GUI through the node stream. The one value the hub has to DERIVE — the
/// status badge — is a control declared at once and FED by <see cref="StatusFeed"/>, which builds
/// no control. The page is on screen at the first render and follows every later edit.</para>
/// </summary>
public static class ProductNodeLayoutAreas
{
    /// <summary>Registers the product views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddProductNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("ProductOverview")
            .WithView("ProductOverview", ProductOverview)
            .WithView("InventoryStatus", InventoryStatus);

    /// <summary>Product overview with details.</summary>
    /// <param name="host">The area host; the status feed reads its node.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The overview template.</returns>
    [Display(GroupName = "Overview", Order = 0)]
    public static UiControl ProductOverview(LayoutAreaHost host, RenderingContext ctx)
        => OverviewTemplate(host.Hub.Address.ToString(), StatusFeed(host, ListingStatus));

    /// <summary>Product inventory status.</summary>
    /// <param name="host">The area host; the status feed reads its node.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The inventory template.</returns>
    [Display(GroupName = "Inventory", Order = 0)]
    public static UiControl InventoryStatus(LayoutAreaHost host, RenderingContext ctx)
        => InventoryTemplate(host.Hub.Address.ToString(), StatusFeed(host, StockStatus));

    /// <summary>The overview of the product at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The product node.</param>
    /// <param name="status">The listing status (<see cref="ListingStatus"/>), as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath, IObservable<string> status)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithView(Bound(Controls.H2(Pointer(nameof(ProductContent.ProductName))), content))
            .WithView(status.Bind(text => Controls.Badge(text), "listingStatus"))
            .WithView(Controls.LayoutGrid
                .WithView(Card("Product Details", content,
                        ("Product ID", nameof(ProductContent.ProductId)),
                        ("Quantity Per Unit", nameof(ProductContent.QuantityPerUnit)),
                        ("Category ID", nameof(ProductContent.CategoryId))),
                    skin => skin.WithXs(12).WithMd(6))
                .WithView(Card("Pricing", content,
                        ("Unit Price", nameof(ProductContent.UnitPrice)),
                        ("Supplier ID", nameof(ProductContent.SupplierId))),
                    skin => skin.WithXs(12).WithMd(6)));
    }

    /// <summary>The inventory of the product at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The product node.</param>
    /// <param name="stock">The stock status (<see cref="StockStatus"/>), as it changes.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl InventoryTemplate(string nodePath, IObservable<string> stock)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithView(Controls.H2("Inventory Status"))
            .WithView(stock.Bind(text => Controls.Badge(text), "stockStatus"))
            .WithView(Controls.LayoutGrid
                .WithView(Figure("Units in Stock", nameof(ProductContent.UnitsInStock), content), skin => skin.WithXs(12).WithMd(4))
                .WithView(Figure("Units on Order", nameof(ProductContent.UnitsOnOrder), content), skin => skin.WithXs(12).WithMd(4))
                .WithView(Figure("Reorder Level", nameof(ProductContent.ReorderLevel), content), skin => skin.WithXs(12).WithMd(4)));
    }

    /// <summary>Active or discontinued — pure.</summary>
    /// <param name="product">The product, or <c>null</c> when the node carries none.</param>
    /// <returns>The badge text.</returns>
    public static string ListingStatus(ProductContent? product) =>
        product is null ? "Product data not available"
        : product.Discontinued ? "Discontinued"
        : "Active";

    /// <summary>Discontinued, low stock (at or below the reorder level) or in stock — pure.</summary>
    /// <param name="product">The product, or <c>null</c> when the node carries none.</param>
    /// <returns>The badge text.</returns>
    public static string StockStatus(ProductContent? product) =>
        product is null ? "Product data not available"
        : product.Discontinued ? "Discontinued"
        : product.UnitsInStock <= product.ReorderLevel ? "Low Stock"
        : "In Stock";

    /// <summary>
    /// The FEED half: <paramref name="derive"/> applied to the node's content on every emission of
    /// the node. Builds no control. A failure is logged and shown as text — reported, never swallowed.
    /// </summary>
    /// <param name="host">The area host whose node is read.</param>
    /// <param name="derive">What to show for the content.</param>
    /// <returns>The derived text.</returns>
    public static IObservable<string> StatusFeed(LayoutAreaHost host, Func<ProductContent?, string> derive) =>
        host.Workspace.GetMeshNodeStream()
            .Select(node => derive(node.ContentAs<ProductContent>(host.Hub.JsonSerializerOptions)))
            .DistinctUntilChanged()
            .Catch<string, Exception>(ex =>
            {
                host.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(nameof(ProductNodeLayoutAreas))
                    .LogWarning(ex, "Product status of {Path} could not be read", host.Hub.Address);
                return Observable.Return("Status could not be read");
            });

    /// <summary>A big bound number over its caption.</summary>
    private static UiControl Figure(string caption, string property, string content) =>
        Controls.Stack
            .WithStyle("padding: 20px; border-radius: 8px; background: var(--neutral-layer-2);")
            .WithView(Bound(Controls.H1(Pointer(property)), content))
            .WithView(Controls.Label(caption).WithStyle("color: var(--neutral-foreground-hint);"));

    /// <summary>A titled card of caption / bound-value pairs.</summary>
    private static UiControl Card(string title, string content, params (string Caption, string Property)[] fields) =>
        fields.Aggregate(
            Controls.Stack
                .WithStyle("padding: 20px; border-radius: 8px; background: var(--neutral-layer-2); gap: 12px;")
                .WithView(Controls.H3(title)),
            (card, field) => card.WithView(Controls.Stack
                .WithView(Controls.Label(field.Caption).WithStyle("font-size: 12px; color: var(--neutral-foreground-hint);"))
                .WithView(Bound(Controls.Body(Pointer(field.Property)), content))));

    /// <summary>A pointer into the content — camelCase, as the hub serializes it.</summary>
    private static JsonPointerReference Pointer(string property) =>
        new(char.ToLowerInvariant(property[0]) + property[1..]);

    private static LabelControl Bound(LabelControl label, string content) => label with { DataContext = content };
}
