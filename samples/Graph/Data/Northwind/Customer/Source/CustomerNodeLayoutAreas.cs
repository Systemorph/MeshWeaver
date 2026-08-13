// <meshweaver>
// Id: CustomerNodeLayoutAreas
// DisplayName: Customer Node Views
// </meshweaver>

using System.Reactive.Linq;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Customer MeshNodes.
/// Displays customer contact information and details.
/// </summary>
public static class CustomerNodeLayoutAreas
{
    public static LayoutDefinition AddCustomerNodeLayoutAreas(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("CustomerOverview")
            .WithView("CustomerOverview", CustomerOverview)
            .WithView("ContactInfo", ContactInfo);

    /// <summary>
    /// The node's customer content. <c>ContentAs</c> — never <c>is</c> + a hand-rolled JSON branch:
    /// the accessor covers the already-typed value AND the degraded JsonElement/JsonNode AND a
    /// same-short-named <c>CustomerContent</c> from another build (every recompile of a dynamic
    /// NodeType mints a new collectible assembly, so "the same" record has a different CLR identity
    /// per build — the case the hand-rolled version had no round-trip to recover, leaving the view
    /// blank after a recompile with nothing to grep).
    /// </summary>
    private static CustomerContent? ExtractCustomerContent(LayoutAreaHost host, MeshNode? node) =>
        node.ContentAs<CustomerContent>(host.Hub.JsonSerializerOptions);

    /// <summary>Customer overview with company details.</summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> CustomerOverview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var customer = ExtractCustomerContent(host, node);

            if (customer == null)
                return (UiControl?)Controls.Markdown("*Customer data not available*");

            var location = string.Join(", ", new[] { customer.City, customer.Region, customer.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown($"## {customer.CompanyName}"))
                .WithView(Controls.Html($@"
                    <div style='display: grid; grid-template-columns: repeat(2, 1fr); gap: 24px; margin: 16px 0;'>
                        <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface);'>
                            <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Company Information</h3>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Customer ID</div>
                                <div style='font-size: 16px; font-weight: 500;'>{customer.CustomerId}</div>
                            </div>
                            <div style='margin-bottom: 12px;'>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Company Name</div>
                                <div style='font-size: 16px; font-weight: 500;'>{customer.CompanyName}</div>
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
                                <div style='font-size: 16px; font-weight: 500;'>{customer.ContactName}</div>
                            </div>
                            <div>
                                <div style='font-size: 12px; color: var(--mud-palette-text-secondary);'>Title</div>
                                <div style='font-size: 16px;'>{customer.ContactTitle}</div>
                            </div>
                        </div>
                    </div>
                "))
                .WithView(Controls.Html($@"
                    <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface); margin-top: 16px;'>
                        <h3 style='margin: 0 0 16px 0; color: var(--mud-palette-primary);'>Contact Information</h3>
                        <div style='display: grid; grid-template-columns: 120px 1fr; gap: 12px;'>
                            <div style='color: var(--mud-palette-text-secondary);'>Address:</div>
                            <div>{string.Join(", ", new[] { customer.City, customer.Region, customer.PostalCode, customer.Country }.Where(s => !string.IsNullOrWhiteSpace(s)))}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Phone:</div>
                            <div>{(string.IsNullOrWhiteSpace(customer.Phone) ? "—" : customer.Phone)}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Fax:</div>
                            <div>{(string.IsNullOrWhiteSpace(customer.Fax) ? "—" : customer.Fax)}</div>
                        </div>
                    </div>
                "));
        });
    }

    /// <summary>Customer contact information.</summary>
    [Display(GroupName = "Contact", Order = 0)]
    public static IObservable<UiControl?> ContactInfo(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var customer = ExtractCustomerContent(host, node);

            if (customer == null)
                return (UiControl?)Controls.Markdown("*Customer data not available*");

            var address = string.Join(", ", new[] { customer.City, customer.Region, customer.PostalCode, customer.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            return (UiControl?)Controls.Stack
                .WithView(Controls.Markdown("## Contact Information"))
                .WithView(Controls.Html($@"
                    <div style='padding: 20px; border-radius: 8px; background: var(--mud-palette-surface); max-width: 600px;'>
                        <div style='display: grid; grid-template-columns: 120px 1fr; gap: 12px;'>
                            <div style='color: var(--mud-palette-text-secondary);'>Address:</div>
                            <div>{address}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Phone:</div>
                            <div>{(string.IsNullOrWhiteSpace(customer.Phone) ? "—" : customer.Phone)}</div>
                            <div style='color: var(--mud-palette-text-secondary);'>Fax:</div>
                            <div>{(string.IsNullOrWhiteSpace(customer.Fax) ? "—" : customer.Fax)}</div>
                        </div>
                    </div>
                "));
        });
    }
}
