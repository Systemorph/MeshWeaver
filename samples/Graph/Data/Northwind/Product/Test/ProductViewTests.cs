// <meshweaver>
// Id: ProductViewTests
// DisplayName: Product Views Tests — the views are templates
// </meshweaver>
#nullable enable
using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

/// <summary>
/// The cases of <c>Northwind/Product</c>'s views: each view is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later") — built without the node, its first emission is the whole page,
/// with no view deferred until data arrives — and it binds the node by PATH. Pure cases: they
/// build the templates from a path and THROW on failure. <see cref="Tests"/> runs them; a case not
/// listed there runs nowhere.
/// </summary>
public static class ProductViewTests
{
    private const string NodePath = "Northwind/Product/Chai";

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame: the <c>N/M passed</c> title and a row per case.</returns>
    public static UiControl Tests(LayoutAreaHost host, RenderingContext context)
    {
        var cases = new (string Name, Action Body)[]
        {
            ("The overview is whole while its status feed is silent, and binds the product by path.", Overview_IsATemplateBoundByPath),
            ("The inventory page is whole while its status feed is silent, and binds the stock figures.", Inventory_IsATemplateBoundByPath),
            ("The derived statuses: low stock is AT or below the reorder level; discontinued wins.", Statuses_AreDerivedFromTheContent),
        };
        var rows = cases.Select(c => Run(c.Name, c.Body)).ToImmutableList();
        return Controls.Stack
            .WithView(Controls.Markdown($"### Product views tests — {rows.Count(r => r.Passed)}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }

    /// <summary>The overview is whole while its status feed is silent, and binds the product by path.</summary>
    public static void Overview_IsATemplateBoundByPath()
    {
        var template = ProductNodeLayoutAreas.OverviewTemplate(NodePath, Observable.Never<string>());
        ExpectTemplate(template, "ProductOverview");
        var bindings = Bindings(template);
        foreach (var pointer in new[] { "productName", "productId", "quantityPerUnit", "categoryId", "unitPrice", "supplierId" })
            Expect(bindings.Any(b => b.Pointer == pointer && b.Context == LayoutAreaReference.GetMeshNodeDataContext(NodePath)),
                $"the overview binds '{pointer}' in the content");
        Expect(LayoutTemplate.Descendants(template).OfType<BadgeControl>()
                .Any(b => b.DataContext == LayoutAreaReference.GetDataPointer("listingStatus")),
            "the status badge is declared up front and fed through /data/listingStatus");
    }

    /// <summary>The inventory page is whole while its status feed is silent, and binds the stock figures.</summary>
    public static void Inventory_IsATemplateBoundByPath()
    {
        var template = ProductNodeLayoutAreas.InventoryTemplate(NodePath, Observable.Never<string>());
        ExpectTemplate(template, "InventoryStatus");
        var bindings = Bindings(template);
        Expect(bindings.Select(b => b.Pointer).SequenceEqual(new[] { "unitsInStock", "unitsOnOrder", "reorderLevel" }),
            $"the three stock figures are bound, got {string.Join(", ", bindings.Select(b => b.Pointer))}");
    }

    /// <summary>The derived statuses: low stock is AT or below the reorder level; discontinued wins.</summary>
    public static void Statuses_AreDerivedFromTheContent()
    {
        var product = new ProductContent { UnitsInStock = 10, ReorderLevel = 10 };
        Expect(ProductNodeLayoutAreas.StockStatus(product) == "Low Stock", "at the reorder level is low stock");
        Expect(ProductNodeLayoutAreas.StockStatus(product with { UnitsInStock = 11 }) == "In Stock", "above it is in stock");
        Expect(ProductNodeLayoutAreas.StockStatus(product with { Discontinued = true }) == "Discontinued", "discontinued wins");
        Expect(ProductNodeLayoutAreas.ListingStatus(product) == "Active", "a listed product is active");
        Expect(ProductNodeLayoutAreas.ListingStatus(null) == "Product data not available", "no content says so");
    }

    /// <summary>The node-bound pointers of the tree's labels, as <c>(pointer, data context)</c>.</summary>
    private static ImmutableList<(string Pointer, string? Context)> Bindings(UiControl root) =>
        LayoutTemplate.Descendants(root)
            .OfType<LabelControl>()
            .Where(label => label.Data is JsonPointerReference)
            .Select(label => (((JsonPointerReference)label.Data).Pointer, label.DataContext))
            .ToImmutableList();

    private static void ExpectTemplate(UiControl template, string view)
    {
        var deferred = LayoutTemplate.DeferredViews(template);
        Expect(deferred.IsEmpty, $"{view} defers {deferred.Count} view(s) until data arrives: {string.Join(", ", deferred)}");
        Expect(!LayoutTemplate.Descendants(template).OfType<HtmlControl>().Any(),
            $"{view} renders hand-built HTML; compose platform controls instead");
    }

    private static ProductViewTestsRow Run(string name, Action body)
    {
        try
        {
            body();
            return new(name, "✅ pass");
        }
        catch (Exception ex)
        {
            return new(name, "❌ " + ex.Message.Replace('\n', ' '));
        }
    }

    private static void Expect(bool condition, string because)
    {
        if (!condition)
            throw new InvalidOperationException(because);
    }
}

/// <summary>One executed case, as a grid row.</summary>
/// <param name="Case">What the case asserts.</param>
/// <param name="Result">✅, or ❌ with the failure message.</param>
public sealed record ProductViewTestsRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
}
