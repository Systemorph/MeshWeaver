// <meshweaver>
// Id: CustomerNodeViews
// DisplayName: Customer Node Views
// </meshweaver>

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

/// <summary>
/// Instance-level views for individual Customer MeshNodes.
/// Displays customer contact information and details.
/// </summary>
public static class CustomerNodeViews
{
    public static LayoutDefinition AddCustomerNodeViews(this LayoutDefinition layout) =>
        layout
            .WithDefaultArea("CustomerOverview")
            .WithView("CustomerOverview", CustomerOverview)
            .WithView("ContactInfo", ContactInfo);

    private static CustomerContent? ExtractCustomerContent(MeshNode? node)
    {
        if (node?.Content == null)
            return null;

        if (node.Content is CustomerContent cc)
            return cc;

        if (node.Content is JsonElement json)
        {
            return new CustomerContent
            {
                CustomerId = json.TryGetProperty("customerId", out var cid) ? cid.GetString() ?? "" : "",
                CompanyName = json.TryGetProperty("companyName", out var cn) ? cn.GetString() ?? "" : "",
                ContactName = json.TryGetProperty("contactName", out var ctn) ? ctn.GetString() ?? "" : "",
                ContactTitle = json.TryGetProperty("contactTitle", out var ct) ? ct.GetString() ?? "" : "",
                City = json.TryGetProperty("city", out var city) ? city.GetString() ?? "" : "",
                Region = json.TryGetProperty("region", out var region) ? region.GetString() ?? "" : "",
                PostalCode = json.TryGetProperty("postalCode", out var pc) ? pc.GetString() ?? "" : "",
                Country = json.TryGetProperty("country", out var country) ? country.GetString() ?? "" : "",
                Phone = json.TryGetProperty("phone", out var phone) ? phone.GetString() ?? "" : "",
                Fax = json.TryGetProperty("fax", out var fax) ? fax.GetString() ?? "" : ""
            };
        }
        return null;
    }

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
            var customer = ExtractCustomerContent(node);

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
            var customer = ExtractCustomerContent(node);

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
