using System.Reactive.Disposables;
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

/// <summary>
/// Blazor view for <c>MeshNodeCollectionControl</c> — renders a live, query-driven list of
/// mesh nodes with inline sub-entry chips, delete, and navigate actions. Each configured
/// query is subscribed independently so the view stays live with the underlying data.
/// </summary>
public partial class MeshNodeCollectionView : BlazorView<MeshNodeCollectionControl, MeshNodeCollectionView>
{
    private List<MeshNode> _items = [];
    private bool _isLoading = true;
    // 🚨 CompositeDisposable, registered in the base's Disposables so component teardown makes it
    // terminal (issue #1308). Two defects in the List<IDisposable> this replaces: DeleteItem
    // re-runs LoadItems from the DeleteNode observable's callback — off the renderer — so the
    // clear-then-refill raced a renderer-thread LoadItems ("Collection was modified"); and nothing
    // ever disposed the list on component teardown, so every query subscription outlived the view.
    private readonly CompositeDisposable _subscriptions = new();

    /// <summary>
    /// Tears down prior per-query subscriptions and starts a fresh live subscription for
    /// each query declared on the view-model, merging results by path.
    /// </summary>
    protected override void BindData()
    {
        base.BindData();
        // Idempotent: Disposables is a set-like composite in effect — re-adding the same instance
        // across re-binds is harmless because the base disposes each entry exactly once, and the
        // composite is only ever disposed at teardown.
        if (!_registeredForDisposal)
        {
            _registeredForDisposal = true;
            Disposables.Add(_subscriptions);
        }
        LoadItems();
    }

    private bool _registeredForDisposal;

    private void LoadItems()
    {
        // Tear down any prior live subscriptions before re-binding. Clear (not Dispose) — the
        // composite must stay usable for the fresh per-query subscriptions below.
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
            var sub = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
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
                    ex =>
                    {
                        // A faulted query stream used to be swallowed (`_ => { }`), leaving the view pinned
                        // on the loading spinner forever. Clear loading so the (possibly empty) result
                        // renders, and surface the fault (log + modal + inline) via the base primitive.
                        _isLoading = false;
                        SurfaceError(ex, $"Loading collection (query '{query}')");
                    });
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
    /// Removes a sub-entry (role or group) from a node's content and persists the change by writing
    /// straight back to the node stream (the canonical <c>GetMeshNodeStream(path).Update(...)</c> path).
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
