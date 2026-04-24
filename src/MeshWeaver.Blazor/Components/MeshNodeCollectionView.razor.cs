using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Components;

public partial class MeshNodeCollectionView : BlazorView<MeshNodeCollectionControl, MeshNodeCollectionView>
{
    private List<MeshNode> _items = [];
    private bool _isLoading = true;
    private readonly List<IDisposable> _subscriptions = new();

    protected override void BindData()
    {
        base.BindData();
        LoadItems();
    }

    private void LoadItems()
    {
        // Tear down any prior live subscriptions before re-binding.
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();

        _isLoading = true;
        _ = InvokeAsync(StateHasChanged);

        var queries = ViewModel?.Queries ?? [];
        if (queries.Length == 0)
        {
            _items = [];
            _isLoading = false;
            _ = InvokeAsync(StateHasChanged);
            return;
        }

        // Per-query live subscription. The view aggregates the latest snapshots across queries
        // (same dedup-by-Path semantics as before) but stays live: any change to the matching
        // sets refreshes the view via the Subscribe callback.
        var perQueryResults = new Dictionary<string, IReadOnlyList<MeshNode>>();
        foreach (var q in queries)
        {
            var query = q;
            var sub = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Subscribe(
                    change =>
                    {
                        perQueryResults[query] = MergeQueryChange(
                            perQueryResults.GetValueOrDefault(query, Array.Empty<MeshNode>()),
                            change);
                        _items = perQueryResults.Values
                            .SelectMany(r => r)
                            .GroupBy(n => n.Path)
                            .Select(g => g.First())
                            .ToList();
                        _isLoading = false;
                        _ = InvokeAsync(StateHasChanged);
                    },
                    _ => { });
            _subscriptions.Add(sub);
        }
    }

    private static IReadOnlyList<MeshNode> MergeQueryChange(IReadOnlyList<MeshNode> current,
        QueryResultChange<MeshNode> change) => change.ChangeType switch
    {
        QueryChangeType.Initial or QueryChangeType.Reset => change.Items,
        QueryChangeType.Added => current.Concat(change.Items).ToList(),
        QueryChangeType.Updated => current
            .Select(n => change.Items.FirstOrDefault(c => c.Path == n.Path) ?? n)
            .ToList(),
        QueryChangeType.Removed => current
            .Where(n => !change.Items.Any(r => r.Path == n.Path))
            .ToList(),
        _ => current
    };

    private void DeleteItem(string nodePath)
    {
        var nodeFactory = Hub!.ServiceProvider.GetRequiredService<IMeshService>();
        nodeFactory.DeleteNode(nodePath).Subscribe(
            (bool _) => LoadItems(),
            (Exception _) => { });
    }

    private void NavigateToItem(string nodePath) => NavigationManager.NavigateTo($"/{nodePath}");

    private void OnAddClick() => OnClick();

    /// <summary>
    /// Extracts sub-entries (roles or groups) from a node's content for inline chip rendering.
    /// Returns null for nodes that don't have recognized sub-entry content.
    /// </summary>
    private static List<SubEntry>? GetSubEntries(MeshNode node)
    {
        if (node.Content is not JsonElement json || json.ValueKind != JsonValueKind.Object)
            return null;

        // Try AccessAssignment.Roles
        if (json.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
        {
            return roles.EnumerateArray()
                .Select((r, i) => new SubEntry(
                    i,
                    GetJsonString(r, "role") ?? $"Role {i}",
                    r.TryGetProperty("denied", out var d) && d.ValueKind == JsonValueKind.True))
                .ToList();
        }

        // Try GroupMembership.Groups
        if (json.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            return groups.EnumerateArray()
                .Select((g, i) => new SubEntry(
                    i,
                    GetJsonString(g, "group") ?? $"Group {i}",
                    false))
                .ToList();
        }

        return null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    /// <summary>
    /// Removes a sub-entry (role or group) from a node's content and persists the change.
    /// Uses the same DataChangeRequest pattern as OverviewLayoutArea.SetupAutoSave.
    /// </summary>
    private void RemoveSubEntry(MeshNode node, int index)
    {
        if (node.Content is not JsonElement json)
            return;

        var jsonObj = JsonNode.Parse(json.GetRawText())?.AsObject();
        if (jsonObj == null)
            return;

        string? arrayProp = null;
        if (jsonObj["roles"] is JsonArray) arrayProp = "roles";
        else if (jsonObj["groups"] is JsonArray) arrayProp = "groups";

        if (arrayProp == null)
            return;

        var arr = jsonObj[arrayProp]!.AsArray();
        if (index < 0 || index >= arr.Count)
            return;

        arr.RemoveAt(index);

        var updatedContent = JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString());
        var updatedNode = node with { Content = updatedContent };

        if (!string.IsNullOrEmpty(node.Namespace))
        {
            var targetAddress = new Address(node.Namespace);
            Hub?.Post(
                new DataChangeRequest().WithUpdates(updatedNode),
                o => o.WithTarget(targetAddress));
        }

        LoadItems();
    }

    private record SubEntry(int Index, string Label, bool IsDenied);
}
