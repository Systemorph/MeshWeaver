using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for creating reactive MeshSearchControl with server-side grouping.
/// </summary>
public static class MeshSearchExtensions
{
    /// <summary>
    /// Creates a reactive MeshSearchControl that observes query results and groups them.
    /// All lambdas are evaluated server-side. Returns IObservable&lt;MeshSearchControl&gt; with PrecomputedGroups.
    /// </summary>
    public static IObservable<MeshSearchControl> ObserveMeshSearch<TKey>(
        this IMessageHub hub,
        string query,
        Func<MeshNode, TKey> groupBySelector,
        Func<TKey, string>? groupLabelSelector = null,
        Func<TKey, int>? groupOrderSelector = null,
        Func<TKey, bool>? groupExpandedSelector = null,
        Func<MeshNode, bool>? filterPredicate = null,
        Func<MeshNode, object>? sortBySelector = null,
        bool sortAscending = true,
        Action<MeshSearchControl>? configureControl = null)
    {
        var meshQuery = hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery == null)
            return Observable.Return(new MeshSearchControl());

        return meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan(new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase),
                  (current, change) => ApplyChanges(current, change))
            .Select(dict =>
            {
                var nodes = dict.Values.AsEnumerable();

                // 1. Filter
                if (filterPredicate != null)
                    nodes = nodes.Where(filterPredicate);

                // 2. Sort
                var sorted = sortBySelector != null
                    ? (sortAscending ? nodes.OrderBy(sortBySelector) : nodes.OrderByDescending(sortBySelector))
                    : nodes.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name);

                var sortedList = sorted.ToList();

                // 3. Group and build result
                var groups = sortedList
                    .GroupBy(groupBySelector)
                    .Select(g => new SearchResultGroup
                    {
                        GroupKey = g.Key?.ToString() ?? "",
                        Label = groupLabelSelector?.Invoke(g.Key) ?? g.Key?.ToString() ?? "Items",
                        Order = groupOrderSelector?.Invoke(g.Key) ?? 0,
                        IsExpanded = groupExpandedSelector?.Invoke(g.Key) ?? true,
                        Items = g.Cast<object>().ToList(),
                        TotalCount = g.Count()
                    })
                    .OrderBy(g => g.Order)
                    .ThenBy(g => g.Label)
                    .ToList();

                var control = new MeshSearchControl()
                    .WithShowSearchBox(false)
                    .WithPrecomputedGroups(new GroupedSearchResult
                    {
                        Groups = groups,
                        TotalItems = sortedList.Count
                    });

                configureControl?.Invoke(control);
                return control;
            });
    }

    /// <summary>
    /// Overload for simple string-based grouping (extracts property from MeshNode/Content).
    /// </summary>
    public static IObservable<MeshSearchControl> ObserveMeshSearchByProperty(
        this IMessageHub hub,
        string query,
        string groupByProperty,
        Func<string?, string>? groupLabelSelector = null,
        Func<string?, int>? groupOrderSelector = null,
        Func<string?, bool>? groupExpandedSelector = null,
        Func<MeshNode, bool>? filterPredicate = null)
    {
        return hub.ObserveMeshSearch(
            query,
            groupBySelector: n => GetPropertyValue(n, groupByProperty),
            groupLabelSelector: groupLabelSelector,
            groupOrderSelector: groupOrderSelector,
            groupExpandedSelector: groupExpandedSelector,
            filterPredicate: filterPredicate);
    }

    /// <summary>
    /// Applies query result changes to the cumulative node dictionary.
    /// </summary>
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

    /// <summary>
    /// Extracts a property value from MeshNode or its Content.
    /// </summary>
    public static string? GetPropertyValue(MeshNode node, string property)
    {
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
            if (json.TryGetProperty(property, out var prop))
                return GetJsonValue(prop);

            if (property.Length > 0)
            {
                var camelCase = char.ToLowerInvariant(property[0]) + property.Substring(1);
                if (json.TryGetProperty(camelCase, out var camelProp))
                    return GetJsonValue(camelProp);

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
