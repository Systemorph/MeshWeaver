// <meshweaver>
// Id: SupplierViewTests
// DisplayName: Supplier Views Tests — the views are templates bound by path
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
/// The cases of <c>Northwind/Supplier</c>'s views: each view is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later") — built without the node, its first emission is the whole page,
/// with no view deferred until data arrives — and it binds the node by PATH. Pure cases: they
/// build the templates from a path and THROW on failure. <see cref="Tests"/> runs them; a case not
/// listed there runs nowhere.
/// </summary>
public static class SupplierViewTests
{
    private const string NodePath = "Northwind/Supplier/Exotic_Liquids";

    /// <summary>The Tests view — registered in the NodeType's configuration as <c>WithView("Tests", …)</c>.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="context">The rendering context.</param>
    /// <returns>One verdict frame: the <c>N/M passed</c> title and a row per case.</returns>
    public static UiControl Tests(LayoutAreaHost host, RenderingContext context)
    {
        var cases = new (string Name, Action Body)[]
        {
            ("The overview is a template bound to the supplier's content by path.", Overview_IsATemplateBoundByPath),
            ("The contact page is a template bound to the supplier's content by path.", ContactInfo_IsATemplateBoundByPath),
        };
        var rows = cases.Select(c => Run(c.Name, c.Body)).ToImmutableList();
        return Controls.Stack
            .WithView(Controls.Markdown($"### Supplier views tests — {rows.Count(r => r.Passed)}/{rows.Count} passed"))
            .WithView(host.ToDataGrid(rows));
    }

    /// <summary>The overview is a template bound to the supplier's content by path.</summary>
    public static void Overview_IsATemplateBoundByPath()
    {
        var template = SupplierNodeLayoutAreas.OverviewTemplate(NodePath);
        ExpectTemplate(template, "SupplierOverview");
        var bindings = Bindings(template);
        Expect(bindings.All(b => b.Context == LayoutAreaReference.GetMeshNodeDataContext(NodePath)),
            "every bound value resolves against the supplier's content");
        foreach (var pointer in new[] { "companyName", "supplierId", "contactName", "contactTitle", "city", "country" })
            Expect(bindings.Any(b => b.Pointer == pointer), $"the overview binds '{pointer}'");
    }

    /// <summary>The contact page is a template bound to the supplier's content by path.</summary>
    public static void ContactInfo_IsATemplateBoundByPath()
    {
        var template = SupplierNodeLayoutAreas.ContactTemplate(NodePath);
        ExpectTemplate(template, "ContactInfo");
        var bindings = Bindings(template);
        Expect(bindings.Select(b => b.Pointer).SequenceEqual(new[] { "city", "region", "country", "phone" }),
            $"the address and phone are bound, got {string.Join(", ", bindings.Select(b => b.Pointer))}");
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

    private static SupplierViewTestsRow Run(string name, Action body)
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
public sealed record SupplierViewTestsRow(string Case, string Result)
{
    /// <summary>Whether the case passed.</summary>
    public bool Passed => Result.StartsWith("✅", StringComparison.Ordinal);
}
