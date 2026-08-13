// <meshweaver>
// Id: SupplierNodeLayoutAreas
// DisplayName: Supplier Node Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Supplier MeshNodes.
/// Displays supplier company information and contact details.
/// </summary>
public static class SupplierNodeLayoutAreas
{
    public static LayoutDefinition AddSupplierNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("SupplierOverview")
            .WithView("SupplierOverview", SupplierOverview)
            .WithView("ContactInfo", ContactInfo);

    /// <summary>
    /// The node's supplier content. <c>ContentAs</c> — never <c>is</c> + a hand-rolled JSON branch:
    /// the accessor covers the already-typed value AND the degraded JsonElement/JsonNode AND a
    /// same-short-named <c>SupplierContent</c> from another build (every recompile of a dynamic
    /// NodeType mints a new collectible assembly, so "the same" record has a different CLR identity
    /// per build — the case the hand-rolled version had no round-trip to recover, leaving the view
    /// blank after a recompile with nothing to grep).
    /// </summary>
    private static SupplierContent? ExtractSupplierContent(LayoutAreaHost host, MeshNode? node) =>
        node.ContentAs<SupplierContent>(host.Hub.JsonSerializerOptions);

    /// <summary>Supplier overview with company details.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> SupplierOverview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var supplier = ExtractSupplierContent(host, node);

            if (supplier == null)
                return (UiControl?)Controls.Markdown("*Supplier data not available*");

            var location = string.Join(", ", new[] { supplier.City, supplier.Region, supplier.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown($"## {supplier.CompanyName}"))
                .WithView(Controls.Html($@"
                    <div style='display: grid; grid-template-columns: repeat(2, 1fr); gap: 24px; margin: 16px 0;'>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Company Information</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Supplier ID</div>
                                <div style='font-size: 16px; font-weight: 500;'>{supplier.SupplierId}</div>
                            </div>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Company Name</div>
                                <div style='font-size: 16px; font-weight: 500;'>{supplier.CompanyName}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Location</div>
                                <div style='font-size: 16px;'>{location}</div>
                            </div>
                        </div>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Primary Contact</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Contact Name</div>
                                <div style='font-size: 16px; font-weight: 500;'>{supplier.ContactName}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Title</div>
                                <div style='font-size: 16px;'>{supplier.ContactTitle}</div>
                            </div>
                        </div>
                    </div>
                "));
        });
    }

    /// <summary>Supplier contact information.</summary>
    [Display(GroupName = "Contact", Order = 0)]
    public static IObservable<UiControl?> ContactInfo(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var supplier = ExtractSupplierContent(host, node);

            if (supplier == null)
                return (UiControl?)Controls.Markdown("*Supplier data not available*");

            var address = string.Join(", ", new[] { supplier.City, supplier.Region, supplier.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown("## Contact Information"))
                .WithView(Controls.Html($@"
                    <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface); max-width: 600px;'>
                        <div style='display: grid; grid-template-columns: 120px 1fr; gap: 12px;'>
                            <div style='color: var(--mud-palette-text-secondary);'>Address:</div>
                            <div>{address}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Phone:</div>
                            <div>{(string.IsNullOrWhiteSpace(supplier.Phone) ? "—" : supplier.Phone)}</div>
                        </div>
                    </div>
                "));
        });
    }
}
