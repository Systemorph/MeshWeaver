// <meshweaver>
// Id: SupplierNodeLayoutAreas
// DisplayName: Supplier Node Views
// </meshweaver>

using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Supplier MeshNodes.
///
/// <para>Both views are TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"): built from
/// the node's PATH alone, they emit the whole page on the first render, and every value is a
/// <see cref="JsonPointerReference"/> into the node's <see cref="SupplierContent"/> that the GUI
/// resolves through the node stream — a skeleton until the node arrives, then live. Nothing here
/// reads the node on the hub.</para>
/// </summary>
public static class SupplierNodeLayoutAreas
{
    /// <summary>Registers the supplier views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddSupplierNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("SupplierOverview")
            .WithView("SupplierOverview", SupplierOverview)
            .WithView("ContactInfo", ContactInfo);

    /// <summary>Supplier overview with company details.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The overview template.</returns>
    [Display(GroupName = "Overview", Order = 0)]
    public static UiControl SupplierOverview(LayoutAreaHost host, RenderingContext ctx)
        => OverviewTemplate(host.Hub.Address.ToString());

    /// <summary>Supplier contact information.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The contact template.</returns>
    [Display(GroupName = "Contact", Order = 0)]
    public static UiControl ContactInfo(LayoutAreaHost host, RenderingContext ctx)
        => ContactTemplate(host.Hub.Address.ToString());

    /// <summary>The overview of the supplier at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The supplier node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithView(Bound(Controls.H2(Pointer(nameof(SupplierContent.CompanyName))), content))
            .WithView(Controls.LayoutGrid
                .WithView(Card("Company Information", content,
                        ("Supplier ID", nameof(SupplierContent.SupplierId)),
                        ("Company Name", nameof(SupplierContent.CompanyName)),
                        ("City", nameof(SupplierContent.City)),
                        ("Country", nameof(SupplierContent.Country))),
                    skin => skin.WithXs(12).WithMd(6))
                .WithView(Card("Primary Contact", content,
                        ("Contact Name", nameof(SupplierContent.ContactName)),
                        ("Title", nameof(SupplierContent.ContactTitle))),
                    skin => skin.WithXs(12).WithMd(6)));
    }

    /// <summary>The contact information of the supplier at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The supplier node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl ContactTemplate(string nodePath) =>
        Controls.Stack
            .WithView(Controls.H2("Contact Information"))
            .WithView(Card("Contact Information", LayoutAreaReference.GetMeshNodeDataContext(nodePath),
                ("City", nameof(SupplierContent.City)),
                ("Region", nameof(SupplierContent.Region)),
                ("Country", nameof(SupplierContent.Country)),
                ("Phone", nameof(SupplierContent.Phone))));

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
