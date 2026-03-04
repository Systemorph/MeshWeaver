// <meshweaver>
// Id: InsuranceLayoutAreas
// DisplayName: Insurance Views
// </meshweaver>

using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

/// <summary>
/// Catalog views for ACME Insurance portfolio using CatalogControl with LayoutAreaControl thumbnails.
/// </summary>
public static class InsuranceLayoutAreas
{
    /// <summary>
    /// Registers all ACME Insurance views with the layout definition.
    /// </summary>
    public static LayoutDefinition AddInsuranceLayoutAreas(this LayoutDefinition layout) =>
        layout.WithView("PricingCatalog", PricingCatalog);

    private static Dictionary<string, MeshNode> ApplyChanges(
        Dictionary<string, MeshNode> current, QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeshNode>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var item in change.Items)
        {
            if (change.ChangeType == QueryChangeType.Removed) result.Remove(item.Path);
            else result[item.Path] = item;
        }
        return result;
    }

    private static string? GetProp(MeshNode node, string prop)
    {
        if (node.Content is not JsonElement json) return null;
        if (json.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        var pascal = char.ToUpperInvariant(prop[0]) + prop.Substring(1);
        if (json.TryGetProperty(pascal, out var pp) && pp.ValueKind == JsonValueKind.String) return pp.GetString();
        return null;
    }

    private static ImmutableList<UiControl> Thumbnails(IEnumerable<MeshNode> nodes) =>
        nodes.Select(n => (UiControl)new LayoutAreaControl(n.Path, new LayoutAreaReference("Thumbnail"))
            .WithSpinnerType(SpinnerType.Dots)).ToImmutableList();

    /// <summary>
    /// Pricings grouped by status.
    /// </summary>
    [Display(GroupName = "Overview", Order = 0)]
    public static IObservable<UiControl?> PricingCatalog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var query = $"path:{hubPath} nodeType:Cornerstone/Pricing state:Active scope:subtree";

        var statuses = host.Workspace.GetObservable<PricingStatus>()
            .StartWith(PricingStatus.All.ToList())
            .Select(s => s.OrderBy(x => x.Order).ToList());

        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();

        var nodes = meshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(statuses, (dict, statusList) =>
        {
            var groups = statusList.Select(status =>
            {
                var items = dict.Values
                    .Where(n => (GetProp(n, "status") ?? "Draft") == status.Id)
                    .OrderBy(n => n.Name).ToList();
                return items.Any() ? new CatalogGroup
                {
                    Key = status.Id,
                    Label = status.Name,
                    Emoji = status.Emoji,
                    Order = status.Order,
                    IsExpanded = status.IsExpandedByDefault,
                    Items = Thumbnails(items),
                    TotalCount = items.Count
                } : null;
            }).Where(g => g != null).Cast<CatalogGroup>().ToImmutableList();

            if (!groups.Any())
            {
                return (UiControl?)Controls.Markdown("*No pricings available. Create a new pricing to get started.*");
            }

            return (UiControl?)new CatalogControl().WithGroups(groups);
        });
    }
}
