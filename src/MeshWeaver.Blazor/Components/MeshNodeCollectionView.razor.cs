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

    protected override void BindData()
    {
        base.BindData();
        _ = LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        _isLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var queries = ViewModel?.Queries ?? [];
            if (queries.Length == 0)
            {
                _items = [];
                return;
            }

            var tasks = queries.Select(async q =>
            {
                try
                {
                    return await MeshQuery.QueryAsync<MeshNode>(q).ToListAsync();
                }
                catch
                {
                    return new List<MeshNode>();
                }
            });

            var results = await Task.WhenAll(tasks);
            _items = results
                .SelectMany(r => r)
                .GroupBy(n => n.Path)
                .Select(g => g.First())
                .ToList();
        }
        catch
        {
            _items = [];
        }
        finally
        {
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task DeleteItem(string nodePath)
    {
        var nodeFactory = Hub!.ServiceProvider.GetRequiredService<IMeshNodeFactory>();
        await nodeFactory.DeleteNodeAsync(nodePath);
        await LoadItemsAsync();
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
    private async Task RemoveSubEntry(MeshNode node, int index)
    {
        if (node.Content is not JsonElement json)
            return;

        var jsonObj = JsonNode.Parse(json.GetRawText())?.AsObject();
        if (jsonObj == null)
            return;

        // Determine which array to modify
        string? arrayProp = null;
        if (jsonObj["roles"] is JsonArray) arrayProp = "roles";
        else if (jsonObj["groups"] is JsonArray) arrayProp = "groups";

        if (arrayProp == null)
            return;

        var arr = jsonObj[arrayProp]!.AsArray();
        if (index < 0 || index >= arr.Count)
            return;

        arr.RemoveAt(index);

        // Build updated node with modified content
        var updatedContent = JsonSerializer.Deserialize<JsonElement>(jsonObj.ToJsonString());
        var updatedNode = node with { Content = updatedContent };

        // Persist via DataChangeRequest targeting the node's hub (namespace address)
        if (!string.IsNullOrEmpty(node.Namespace))
        {
            var targetAddress = new Address(node.Namespace);
            Hub?.Post(
                new DataChangeRequest().WithUpdates(updatedNode),
                o => o.WithTarget(targetAddress));
        }

        await LoadItemsAsync();
    }

    private record SubEntry(int Index, string Label, bool IsDenied);
}
