// <meshweaver>
// Id: ProjectViews
// DisplayName: Project Views
// </meshweaver>

using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh.Services;

/// <summary>
/// Catalog views for Project nodes using reactive LINQ with IMeshQuery.
/// </summary>
public static class ProjectViews
{
    /// <summary>
    /// TodosByCategory view grouping items by category.
    /// Uses CatalogControl with thumbnails for each item.
    /// </summary>
    [Display(GroupName = "Overview", Order = 1)]
    public static IObservable<UiControl?> TodosByCategory(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var query = $"path:{hubPath}/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";

        // Get categories from workspace for emoji lookup
        var categories = host.Workspace.GetObservable<Category>()
            .Select(c => c.ToDictionary(cat => cat.Id, cat => cat));

        var nodes = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges);

        return nodes.CombineLatest(categories, (nodeDict, catDict) =>
        {
            var groups = nodeDict.Values
                .OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name)
                .GroupBy(n => GetContentProperty(n, "category") ?? "Uncategorized")
                .Select(g =>
                {
                    var category = catDict.GetValueOrDefault(g.Key) ?? Category.Uncategorized;
                    return new CatalogGroup
                    {
                        Key = g.Key,
                        Label = category.Name,
                        Emoji = category.Emoji,
                        Order = category.Order,
                        IsExpanded = true,
                        Items = g.Select(n => (UiControl)new LayoutAreaControl(n.Path, new LayoutAreaReference("Thumbnail")).WithSpinnerType(SpinnerType.Dots)).ToImmutableList(),
                        TotalCount = g.Count()
                    };
                })
                .OrderBy(g => g.Order)
                .ToImmutableList();

            return (UiControl?)new CatalogControl()
                .WithGroups(groups);
        });
    }

    /// <summary>
    /// Backlog view showing unassigned tasks grouped by priority.
    /// Uses CatalogControl with thumbnails for each item.
    /// </summary>
    [Display(GroupName = "Planning", Order = 1)]
    public static IObservable<UiControl?> Backlog(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var query = $"path:{hubPath}/Todo nodeType:ACME/Project/Todo state:Active scope:subtree";

        return host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>()
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase), ApplyChanges)
            .Select(nodeDict =>
            {
                var groups = nodeDict.Values
                    .Where(n => string.IsNullOrEmpty(GetContentProperty(n, "assignee")) &&
                               GetContentProperty(n, "status") != "Completed")
                    .OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name)
                    .GroupBy(n => GetContentProperty(n, "priority") ?? "Unset")
                    .Select(g => new CatalogGroup
                    {
                        Key = g.Key,
                        Label = GetPriorityLabel(g.Key),
                        Emoji = GetPriorityEmoji(g.Key),
                        Order = GetPriorityOrder(g.Key),
                        IsExpanded = g.Key == "Critical" || g.Key == "High",
                        Items = g.Select(n => (UiControl)new LayoutAreaControl(n.Path, new LayoutAreaReference("Thumbnail")).WithSpinnerType(SpinnerType.Dots)).ToImmutableList(),
                        TotalCount = g.Count()
                    })
                    .OrderBy(g => g.Order)
                    .ToImmutableList();

                return (UiControl?)new CatalogControl()
                    .WithGroups(groups);
            });
    }

    private static Dictionary<string, MeshNode> ApplyChanges(
        Dictionary<string, MeshNode> current,
        QueryResultChange<MeshNode> change)
    {
        var result = change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset
            ? new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MeshNode>(current, StringComparer.OrdinalIgnoreCase);

        foreach (var item in change.Items)
        {
            if (change.ChangeType == QueryChangeType.Removed)
                result.Remove(item.Path);
            else
                result[item.Path] = item;
        }
        return result;
    }

    private static string? GetContentProperty(MeshNode node, string property)
    {
        if (node.Content is not JsonElement json)
            return null;

        if (json.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();

        // Try PascalCase
        var pascalCase = char.ToUpperInvariant(property[0]) + property.Substring(1);
        if (json.TryGetProperty(pascalCase, out var pascalProp) && pascalProp.ValueKind == JsonValueKind.String)
            return pascalProp.GetString();

        return null;
    }

    private static int GetPriorityOrder(string? priority) => priority switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 4
    };

    private static string GetPriorityLabel(string? priority) => priority switch
    {
        "Critical" => "Critical Priority",
        "High" => "High Priority",
        "Medium" => "Medium Priority",
        "Low" => "Low Priority",
        _ => "Unset Priority"
    };

    private static string GetPriorityEmoji(string? priority) => priority switch
    {
        "Critical" => "\ud83d\udea8",
        "High" => "\ud83d\udd25",
        "Medium" => "\ud83d\udfe1",
        "Low" => "\ud83d\udfe2",
        _ => "\u2753"
    };
}
