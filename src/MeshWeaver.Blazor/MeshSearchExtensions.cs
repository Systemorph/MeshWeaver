using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;

namespace MeshWeaver.Blazor;

/// <summary>
/// Extension methods for processing MeshSearchControl grouping and sorting.
/// </summary>
public static class MeshSearchExtensions
{
    /// <summary>
    /// Processes a list of nodes according to the MeshSearchControl configuration,
    /// producing a fully serializable GroupedSearchResult.
    /// </summary>
    public static GroupedSearchResult ProcessResults(
        this MeshSearchControl control,
        IEnumerable<MeshNode> nodes)
    {
        var nodeList = nodes.ToList();
        var grouping = control.Grouping;
        var sections = control.Sections;
        var sorting = control.Sorting;

        // Apply sorting first
        var sortedNodes = ApplySorting(nodeList, sorting);

        // If no grouping configured, group by NodeType as fallback (avoids "Results" header)
        var groupByProperty = grouping?.GroupByProperty;
        if (string.IsNullOrEmpty(groupByProperty))
        {
            groupByProperty = "NodeType"; // Default to NodeType grouping
        }

        // Group by property
        var groupKeyFormatter = grouping?.GroupKeyFormatter as Func<string?, string>;
        var groupKeyOrder = grouping?.GroupKeyOrder as Func<string?, int>;
        var groupExpandedPredicate = grouping?.GroupExpandedPredicate as Func<string?, bool>;

        var groups = sortedNodes
            .GroupBy(n => GetPropertyValue(n, groupByProperty) ?? "")
            .Select(g =>
            {
                var groupKey = g.Key;
                var label = groupKeyFormatter?.Invoke(groupKey) ?? groupKey;

                // Use NodeType as fallback label instead of "Uncategorized"
                if (string.IsNullOrEmpty(label))
                {
                    // Try to get a meaningful label from the first item's NodeType
                    var firstNode = g.FirstOrDefault();
                    label = firstNode?.NodeType?.Split('/').LastOrDefault() ?? "Items";
                }

                var order = groupKeyOrder?.Invoke(groupKey) ?? 0;
                var isExpanded = groupExpandedPredicate?.Invoke(groupKey) ?? true;
                var items = g.ToList();

                var limitedItems = sections?.ItemLimit.HasValue == true
                    ? items.Take(sections.ItemLimit.Value).ToList()
                    : items;

                return new SearchResultGroup
                {
                    GroupKey = groupKey,
                    Label = label,
                    Order = order,
                    IsExpanded = isExpanded,
                    Items = limitedItems.Cast<object>().ToList(),
                    TotalCount = items.Count
                };
            })
            .OrderBy(g => g.Order)
            .ThenBy(g => g.Label)
            .ToList();

        return new GroupedSearchResult
        {
            Groups = groups,
            TotalItems = sortedNodes.Count
        };
    }

    /// <summary>
    /// Builds a "Show more" href for a group using the control's configuration.
    /// </summary>
    public static string? GetShowMoreHref(this MeshSearchControl control, string groupKey)
    {
        var hrefBuilder = control.Sections?.ShowMoreHrefBuilder as Func<string, string>;
        return hrefBuilder?.Invoke(groupKey);
    }

    private static List<MeshNode> ApplySorting(List<MeshNode> nodes, SortConfig? sorting)
    {
        if (sorting == null || string.IsNullOrEmpty(sorting.SortByProperty))
        {
            return nodes.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name).ToList();
        }

        var sorted = sorting.Ascending
            ? nodes.OrderBy(n => GetSortValue(n, sorting.SortByProperty))
            : nodes.OrderByDescending(n => GetSortValue(n, sorting.SortByProperty));

        if (!string.IsNullOrEmpty(sorting.ThenByProperty))
        {
            sorted = sorting.ThenByAscending
                ? ((IOrderedEnumerable<MeshNode>)sorted).ThenBy(n => GetSortValue(n, sorting.ThenByProperty))
                : ((IOrderedEnumerable<MeshNode>)sorted).ThenByDescending(n => GetSortValue(n, sorting.ThenByProperty));
        }

        return sorted.ToList();
    }

    private static object? GetSortValue(MeshNode node, string property)
    {
        var value = GetPropertyValue(node, property);

        // Try to parse as DateTime for proper sorting
        if (DateTime.TryParse(value, out var dateValue))
            return dateValue;

        // Try to parse as number
        if (double.TryParse(value, out var numValue))
            return numValue;

        return value;
    }

    /// <summary>
    /// Extracts a property value from MeshNode or its Content.
    /// </summary>
    public static string? GetPropertyValue(MeshNode node, string property)
    {
        // Check MeshNode properties first
        return property switch
        {
            "Category" or "category" => node.Category,
            "NodeType" or "nodeType" => node.NodeType,
            "Name" or "name" => node.Name,
            "Description" or "description" => node.Description,
            "Path" or "path" => node.Path,
            "Id" or "id" => node.Id,
            _ => GetContentProperty(node.Content, property)
        };
    }

    private static string? GetContentProperty(object? content, string property)
    {
        if (content == null) return null;

        if (content is JsonElement json)
        {
            // Try exact property name
            if (json.TryGetProperty(property, out var prop))
                return GetJsonValue(prop);

            // Try camelCase
            if (property.Length > 0)
            {
                var camelCase = char.ToLowerInvariant(property[0]) + property.Substring(1);
                if (json.TryGetProperty(camelCase, out var camelProp))
                    return GetJsonValue(camelProp);

                // Try PascalCase
                var pascalCase = char.ToUpperInvariant(property[0]) + property.Substring(1);
                if (json.TryGetProperty(pascalCase, out var pascalProp))
                    return GetJsonValue(pascalProp);
            }
        }

        return null;
    }

    private static string? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}
