// <meshweaver>
// Id: CustomerNodeLayoutAreas
// DisplayName: Customer Node Views
// </meshweaver>

using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Customer MeshNodes.
///
/// <para>Both views are TEMPLATES (Doc/GUI/DataBinding → "Templates first, data later"): built from
/// the node's PATH alone, they emit the whole page on the first render, and every value is a
/// <see cref="JsonPointerReference"/> into the node's <see cref="CustomerContent"/> that the GUI
/// resolves through the node stream — a skeleton until the node arrives, then live. Nothing here
/// reads the node on the hub.</para>
/// </summary>
public static class CustomerNodeLayoutAreas
{
    /// <summary>Registers the customer views.</summary>
    /// <param name="layout">The layout definition.</param>
    /// <returns>The layout definition with the views added.</returns>
    public static LayoutDefinition AddCustomerNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("CustomerOverview")
            .WithView("CustomerOverview", CustomerOverview)
            .WithView("ContactInfo", ContactInfo);

    /// <summary>Customer overview with company details.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The overview template.</returns>
    [Display(GroupName = "Overview", Order = 0)]
    public static UiControl CustomerOverview(LayoutAreaHost host, RenderingContext ctx)
        => OverviewTemplate(host.Hub.Address.ToString());

    /// <summary>Customer contact information.</summary>
    /// <param name="host">The area host; only its address is read.</param>
    /// <param name="ctx">The rendering context.</param>
    /// <returns>The contact template.</returns>
    [Display(GroupName = "Contact", Order = 0)]
    public static UiControl ContactInfo(LayoutAreaHost host, RenderingContext ctx)
        => ContactTemplate(host.Hub.Address.ToString());

    /// <summary>The overview of the customer at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The customer node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl OverviewTemplate(string nodePath)
    {
        var content = LayoutAreaReference.GetMeshNodeDataContext(nodePath);
        return Controls.Stack
            .WithView(Bound(Controls.H2(Pointer(nameof(CustomerContent.CompanyName))), content))
            .WithView(Controls.LayoutGrid
                .WithView(Card("Company Information", content,
                        ("Customer ID", nameof(CustomerContent.CustomerId)),
                        ("Company Name", nameof(CustomerContent.CompanyName)),
                        ("City", nameof(CustomerContent.City)),
                        ("Country", nameof(CustomerContent.Country))),
                    skin => skin.WithXs(12).WithMd(6))
                .WithView(Card("Primary Contact", content,
                        ("Contact Name", nameof(CustomerContent.ContactName)),
                        ("Title", nameof(CustomerContent.ContactTitle))),
                    skin => skin.WithXs(12).WithMd(6)))
            .WithView(AddressCard(content));
    }

    /// <summary>The contact information of the customer at <paramref name="nodePath"/>, bound by path.</summary>
    /// <param name="nodePath">The customer node.</param>
    /// <returns>The complete control tree — it never waits on data.</returns>
    public static UiControl ContactTemplate(string nodePath) =>
        Controls.Stack
            .WithView(Controls.H2("Contact Information"))
            .WithView(AddressCard(LayoutAreaReference.GetMeshNodeDataContext(nodePath)));

    private static UiControl AddressCard(string content) =>
        Card("Contact Information", content,
            ("City", nameof(CustomerContent.City)),
            ("Region", nameof(CustomerContent.Region)),
            ("Postal Code", nameof(CustomerContent.PostalCode)),
            ("Country", nameof(CustomerContent.Country)),
            ("Phone", nameof(CustomerContent.Phone)),
            ("Fax", nameof(CustomerContent.Fax)));

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
