using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;


namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Renders MeshSearchControl. Supports two modes:
/// 1. PrecomputedGroups (new) - server-side grouping, just renders results
/// 2. HiddenQuery/VisibleQuery (legacy) - client-side query via IMeshService
/// </summary>
public partial class MeshSearchView : IDisposable
{
    private MonacoEditorView? monacoEditor;
    private string _currentValue = "";
    private bool _initialized;
    private bool _isLoading = true;
    private List<MeshNode> _nodes = new();
    private GroupedSearchResult? _computedGroups;
    private IDisposable? _reactiveSubscription;
    private HashSet<string> _collapsedGroups = new();
    private string _lastBoundVisibleQuery = "";
    private string _lastBoundHiddenQuery = "";
    private bool _showSearchOptions;
    private string _editableHiddenQuery = "";
    private string? _overriddenHiddenQuery;

    [Inject]
    private IMeshService MeshQuery { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private MeshWeaver.Messaging.IMessageHub Hub { get; set; } = default!;

    [Parameter]
    public MeshSearchControl? ViewModel { get; set; }

    [Parameter]
    public ISynchronizationStream<JsonElement>? Stream { get; set; }

    [Parameter]
    public string? Area { get; set; }

    /// <summary>
    /// When set, clicking a card selects the node instead of navigating.
    /// Used by MeshNodePickerView.
    /// </summary>
    [Parameter]
    public EventCallback<MeshNode> OnNodeSelected { get; set; }

    /// <summary>
    /// The path of the currently selected node, used to highlight the selected card.
    /// </summary>
    [Parameter]
    public string? SelectedPath { get; set; }

    // Basic properties
    private string? BoundTitle => ViewModel?.Title?.ToString();
    private string BoundHiddenQuery => _overriddenHiddenQuery ?? ViewModel?.HiddenQuery?.ToString() ?? "";
    private string BoundVisibleQuery => ViewModel?.VisibleQuery?.ToString() ?? "";
    private string BoundPlaceholder => ViewModel?.Placeholder?.ToString() ?? "Search...";
    private string BoundNamespace => ViewModel?.Namespace?.ToString() ?? "";
    private bool BoundShowSearchBox
    {
        get
        {
            if (ViewModel?.ShowSearchBox is bool show) return show;
            if (ViewModel?.ShowSearchBox is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.False) return false;
                if (je.ValueKind == JsonValueKind.True) return true;
            }
            return true; // default
        }
    }
    private bool BoundShowEmptyMessage
    {
        get
        {
            if (ViewModel?.ShowEmptyMessage is bool show) return show;
            if (ViewModel?.ShowEmptyMessage is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.False) return false;
                if (je.ValueKind == JsonValueKind.True) return true;
            }
            return true; // default
        }
    }
    private bool BoundShowLoadingIndicator
    {
        get
        {
            if (ViewModel?.ShowLoadingIndicator is bool show) return show;
            if (ViewModel?.ShowLoadingIndicator is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.False) return false;
                if (je.ValueKind == JsonValueKind.True) return true;
            }
            return true; // default
        }
    }
    private bool BoundExcludeBasePath => ViewModel?.ExcludeBasePath is bool exclude ? exclude : true;
    private bool BoundLiveSearch => ViewModel?.LiveSearch is bool live ? live : true;
    private bool BoundReactiveMode
    {
        get
        {
            if (ViewModel?.ReactiveMode is bool b) return b;
            if (ViewModel?.ReactiveMode is JsonElement je)
                return je.ValueKind == JsonValueKind.True;
            return false;
        }
    }
    private MeshSearchRenderMode BoundRenderMode => ViewModel?.RenderMode is MeshSearchRenderMode mode ? mode : MeshSearchRenderMode.Grouped;

    // Config objects
    private SectionConfig? BoundSections => ViewModel?.Sections;
    private GridConfig BoundGrid => ViewModel?.Grid ?? new GridConfig();

    /// <summary>
    /// "Show all" href — auto-generated from the query when ItemLimit is set.
    /// Passes hidden query as hq= and visible query as q= to the Search page.
    /// </summary>
    private string? BoundShowAllHref
    {
        get
        {
            // Explicit href takes priority
            if (!string.IsNullOrEmpty(BoundSections?.ShowAllHref))
                return BoundSections.ShowAllHref;

            // Auto-derive when there's an item limit (meaning results are truncated)
            if (BoundSections?.ItemLimit > 0)
            {
                var parts = new List<string>();
                var hidden = BoundHiddenQuery;
                if (!string.IsNullOrWhiteSpace(hidden))
                    parts.Add($"hq={Uri.EscapeDataString(hidden)}");
                if (!string.IsNullOrWhiteSpace(_currentValue))
                    parts.Add($"q={Uri.EscapeDataString(_currentValue.Trim())}");
                if (parts.Count > 0)
                    return $"/search?{string.Join("&", parts)}";
            }

            return null;
        }
    }

    // Pre-computed groups (from ViewModel)
    private GroupedSearchResult? BoundPrecomputedGroups => ViewModel?.PrecomputedGroups;

    // Check if we're in precomputed mode (server-side) or query mode (client-side)
    private bool IsPrecomputedMode => BoundPrecomputedGroups != null;

    private bool HasResults()
    {
        if (IsPrecomputedMode)
            return BoundPrecomputedGroups!.TotalItems > 0;
        return _nodes.Count > 0;
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (!_initialized && !string.IsNullOrEmpty(BoundVisibleQuery))
        {
            _currentValue = BoundVisibleQuery;
            _lastBoundVisibleQuery = BoundVisibleQuery;
        }

        if (!_initialized)
        {
            _lastBoundHiddenQuery = BoundHiddenQuery;
        }

        // Re-query when VisibleQuery or HiddenQuery changes from parent
        if (_initialized && BoundVisibleQuery != _lastBoundVisibleQuery)
        {
            _lastBoundVisibleQuery = BoundVisibleQuery;
            _currentValue = BoundVisibleQuery;
            if (!IsPrecomputedMode)
            {
                _ = LoadResultsAsync();
            }
        }

        if (_initialized && BoundHiddenQuery != _lastBoundHiddenQuery)
        {
            _lastBoundHiddenQuery = BoundHiddenQuery;
            if (!IsPrecomputedMode)
            {
                _ = LoadResultsAsync();
            }
        }

        // Initialize collapsed state from pre-computed groups
        if (BoundPrecomputedGroups != null && !_initialized)
        {
            foreach (var group in BoundPrecomputedGroups.Groups.Where(g => !g.IsExpanded))
            {
                _collapsedGroups.Add(group.GroupKey);
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _initialized = true;
            try
            {
                if (monacoEditor != null && !string.IsNullOrEmpty(BoundVisibleQuery))
                {
                    await monacoEditor.SetValueAsync(BoundVisibleQuery);
                }
            }
            catch
            {
                // Monaco editor may not be fully initialized yet
            }

            // If we have pre-computed groups, no need to fetch
            if (IsPrecomputedMode)
            {
                _isLoading = false;
                StateHasChanged();
                return;
            }

            // Reactive mode: subscribe to ObserveQuery for live updates
            if (BoundReactiveMode)
            {
                SubscribeToReactiveUpdates();
                return;
            }

            // Client-side query mode (one-shot)
            await LoadResultsAsync();
            StateHasChanged();
        }
    }

    private Task OnValueChanged(string value)
    {
        _currentValue = value;
        if (BoundLiveSearch && !IsPrecomputedMode)
        {
            _ = LoadResultsAsync();
        }
        return Task.CompletedTask;
    }

    private async Task HandleSearch()
    {
        if (monacoEditor != null)
        {
            _currentValue = await monacoEditor.GetValueAsync();
        }

        if (!IsPrecomputedMode)
        {
            await LoadResultsAsync();
        }

        // Update URL if we're on the search page so the URL is shareable
        UpdateSearchUrl();
    }

    private void UpdateSearchUrl()
    {
        var uri = new Uri(Navigation.Uri);
        if (!uri.AbsolutePath.TrimEnd('/').Equals("/search", StringComparison.OrdinalIgnoreCase))
            return;

        var parts = new List<string>();
        var visibleQuery = _currentValue?.Trim();
        if (!string.IsNullOrWhiteSpace(visibleQuery))
            parts.Add($"q={Uri.EscapeDataString(visibleQuery)}");
        var hiddenQuery = BoundHiddenQuery;
        if (!string.IsNullOrWhiteSpace(hiddenQuery))
            parts.Add($"hq={Uri.EscapeDataString(hiddenQuery)}");

        var url = parts.Count > 0 ? $"/search?{string.Join("&", parts)}" : "/search";
        Navigation.NavigateTo(url, replace: true);
    }

    private async Task LoadResultsAsync()
    {
        _isLoading = true;
        StateHasChanged();

        try
        {
            var query = BuildFullQuery();
            _nodes = await MeshQuery.QueryAsync<MeshNode>(query).ToListAsync();

            // Exclude base path node if configured
            if (BoundExcludeBasePath && !string.IsNullOrEmpty(BoundNamespace))
            {
                var basePath = BoundNamespace.Trim('/');
                _nodes = _nodes.Where(n => n.Path != basePath).ToList();
            }

            // Compute groups locally
            if (ViewModel != null)
            {
                _computedGroups = ProcessResults(_nodes);
                InitializeCollapsedState(_computedGroups);
            }
        }
        catch
        {
            _nodes = new List<MeshNode>();
            _computedGroups = null;
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void SubscribeToReactiveUpdates()
    {
        _reactiveSubscription?.Dispose();
        var query = BuildFullQuery();
        var request = MeshQueryRequest.FromQuery(query);

        // Track the current set of paths. MeshSearchView only cares about
        // which nodes exist (structural changes), not their content.
        // Individual cards handle their own content updates via LayoutAreaView.
        var knownPaths = new HashSet<string>();

        _reactiveSubscription = MeshQuery.ObserveQuery<MeshNode>(request)
            .Subscribe(change =>
            {
                // Compute updated path set without touching _nodes yet.
                HashSet<string> newPaths;
                if (change.ChangeType == QueryChangeType.Initial ||
                    change.ChangeType == QueryChangeType.Reset)
                {
                    newPaths = change.Items.Select(n => n.Path!).ToHashSet();
                }
                else if (change.ChangeType == QueryChangeType.Added)
                {
                    newPaths = new HashSet<string>(knownPaths);
                    foreach (var item in change.Items)
                        if (item.Path != null)
                            newPaths.Add(item.Path);
                }
                else if (change.ChangeType == QueryChangeType.Removed)
                {
                    newPaths = new HashSet<string>(knownPaths);
                    foreach (var item in change.Items)
                        if (item.Path != null)
                            newPaths.Remove(item.Path);
                }
                else
                {
                    // Updated — content changed but set of nodes didn't.
                    return;
                }

                // Only re-render when the set of paths actually changed.
                if (!_isLoading && knownPaths.SetEquals(newPaths))
                    return;

                knownPaths = newPaths;

                InvokeAsync(() =>
                {
                    if (change.ChangeType == QueryChangeType.Initial ||
                        change.ChangeType == QueryChangeType.Reset)
                    {
                        _nodes = change.Items.ToList();
                    }
                    else if (change.ChangeType == QueryChangeType.Added)
                    {
                        _nodes.AddRange(change.Items);
                    }
                    else if (change.ChangeType == QueryChangeType.Removed)
                    {
                        var removedPaths = change.Items.Select(n => n.Path).ToHashSet();
                        _nodes.RemoveAll(n => removedPaths.Contains(n.Path));
                    }

                    // Exclude base path if configured
                    if (BoundExcludeBasePath && !string.IsNullOrEmpty(BoundNamespace))
                    {
                        var basePath = BoundNamespace.Trim('/');
                        _nodes = _nodes.Where(n => n.Path != basePath).ToList();
                    }

                    _computedGroups = ProcessResults(_nodes);
                    _isLoading = false;
                    StateHasChanged();
                });
            });
    }

    private GroupedSearchResult ProcessResults(List<MeshNode> nodes)
    {
        var grouping = ViewModel?.Grouping;
        var sections = ViewModel?.Sections;
        var sorting = ViewModel?.Sorting;

        var sortedNodes = ApplySorting(nodes, sorting);

        var groupByProperty = grouping?.GroupByProperty;
        if (string.IsNullOrEmpty(groupByProperty))
            groupByProperty = "NodeType";

        var groups = sortedNodes
            .GroupBy(n => GetPropertyValue(n, groupByProperty) ?? "")
            .Select(g =>
            {
                var groupKey = g.Key;
                var label = groupKey;

                if (string.IsNullOrEmpty(label))
                {
                    var firstNode = g.FirstOrDefault();
                    label = firstNode?.NodeType?.Split('/').LastOrDefault() ?? "Items";
                }

                var items = g.ToList();
                var limitedItems = sections?.ItemLimit.HasValue == true
                    ? items.Take(sections.ItemLimit.Value).ToList()
                    : items;

                return new SearchResultGroup
                {
                    GroupKey = groupKey,
                    Label = label,
                    Order = 0,
                    IsExpanded = true,
                    Items = limitedItems.Cast<object>().ToList(),
                    TotalCount = items.Count
                };
            })
            .OrderBy(g => g.Label)
            .ToList();

        return new GroupedSearchResult
        {
            Groups = groups,
            TotalItems = sortedNodes.Count
        };
    }

    private List<MeshNode> ApplySorting(List<MeshNode> nodes, SortConfig? sorting)
    {
        if (sorting == null || string.IsNullOrEmpty(sorting.SortByProperty))
        {
            // If the query already has a sort: directive, preserve the query order
            // (e.g. source:accessed sort:LastModified-desc should keep access-time order).
            var query = BuildFullQuery();
            if (query.Contains("sort:", StringComparison.OrdinalIgnoreCase))
                return nodes;
            return nodes.OrderBy(n => n.Order).ThenBy(n => n.Name).ToList();
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

    private object? GetSortValue(MeshNode node, string property)
    {
        var value = GetPropertyValue(node, property);
        if (DateTime.TryParse(value, out var dateValue))
            return dateValue;
        if (double.TryParse(value, out var numValue))
            return numValue;
        return value;
    }

    private string? GetPropertyValue(MeshNode node, string property)
    {
        return property switch
        {
            "Category" or "category" => node.Category,
            "NodeType" or "nodeType" => node.NodeType,
            "Name" or "name" => node.Name,
            "Description" or "description" => null,
            "Path" or "path" => node.Path,
            "Id" or "id" => node.Id,
            _ => GetContentProperty(node.Content, property)
        };
    }

    private string? GetContentProperty(object? content, string property)
    {
        if (content == null) return null;
        if (content is not JsonElement json) return null;

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

        return null;
    }

    private string? GetJsonValue(JsonElement element)
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

    private void InitializeCollapsedState(GroupedSearchResult? groups)
    {
        if (groups == null || _initialized) return;

        foreach (var group in groups.Groups.Where(g => !g.IsExpanded))
        {
            _collapsedGroups.Add(group.GroupKey);
        }
    }

    private string BuildFullQuery()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(BoundHiddenQuery))
            parts.Add(BoundHiddenQuery);

        if (!string.IsNullOrWhiteSpace(_currentValue))
            parts.Add(_currentValue.Trim());

        return string.Join(" ", parts);
    }

    private async Task<CompletionItem[]> GetCompletionsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            var searchQuery = string.IsNullOrEmpty(BoundNamespace)
                ? $"*{query}* scope:descendants"
                : $"namespace:{BoundNamespace} *{query}* scope:descendants";

            var request = new MeshQueryRequest { Query = searchQuery, Limit = 10 };
            var results = await MeshQuery.QueryAsync<MeshNode>(request).ToArrayAsync();

            return results.Select(node => new CompletionItem
            {
                Label = node.Name ?? node.Id,
                InsertText = node.Name ?? node.Id,
                Description = node.NodeType ?? "",
                Path = node.Path,
                Category = node.Category ?? ""
            }).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<SearchResultGroup> GetGroups()
    {
        if (IsPrecomputedMode)
            return BoundPrecomputedGroups!.Groups;
        return _computedGroups?.Groups ?? [];
    }

    private void ToggleGroup(string groupKey)
    {
        if (_collapsedGroups.Contains(groupKey))
            _collapsedGroups.Remove(groupKey);
        else
            _collapsedGroups.Add(groupKey);
        StateHasChanged();
    }

    private string? BoundItemArea
    {
        get
        {
            if (ViewModel?.ItemArea is string s) return s;
            if (ViewModel?.ItemArea is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return null;
        }
    }

    private int? BoundMaxColumns
    {
        get
        {
            if (ViewModel?.MaxColumns is int i) return i;
            if (ViewModel?.MaxColumns is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetInt32();
            return null;
        }
    }

    private string AreaGridStyle
    {
        get
        {
            var maxCols = BoundMaxColumns;
            if (maxCols.HasValue)
                return $"grid-template-columns: repeat({maxCols.Value}, 1fr);";
            return "";
        }
    }

    private bool BoundDisableNavigation
    {
        get
        {
            if (ViewModel?.DisableNavigation is bool b) return b;
            if (ViewModel?.DisableNavigation is JsonElement je)
                return je.ValueKind == JsonValueKind.True;
            return false;
        }
    }

    private string? BoundCreateNodeType
    {
        get
        {
            if (ViewModel?.CreateNodeType is string s) return s;
            if (ViewModel?.CreateNodeType is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return null;
        }
    }

    private string? BoundCreateNamespace
    {
        get
        {
            if (ViewModel?.CreateNamespace is string s) return s;
            if (ViewModel?.CreateNamespace is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            // Fallback: parse from HiddenQuery "namespace:XXX ..."
            var hidden = BoundHiddenQuery;
            if (!string.IsNullOrEmpty(hidden))
            {
                var match = Regex.Match(hidden, @"namespace:(\S+)");
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }
    }

    private string? BoundCreateHref
    {
        get
        {
            if (ViewModel?.CreateHref is string s) return s;
            if (ViewModel?.CreateHref is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return null;
        }
    }

    private bool ShowCreateButton =>
        !string.IsNullOrEmpty(BoundCreateHref) || !string.IsNullOrEmpty(BoundCreateNodeType);

    private void HandleCreateClick()
    {
        // CreateHref takes priority — direct navigation without creating a transient
        var href = BoundCreateHref;
        if (!string.IsNullOrEmpty(href))
        {
            Navigation.NavigateTo(href);
            return;
        }

        var ns = BoundCreateNamespace;
        if (string.IsNullOrEmpty(ns)) return;

        var createUrl = $"/{ns}/{MeshNodeLayoutAreas.CreateNodeArea}";
        var nodeType = BoundCreateNodeType;
        if (!string.IsNullOrEmpty(nodeType))
            createUrl += $"?types={Uri.EscapeDataString(nodeType)}";

        Navigation.NavigateTo(createUrl);
    }

    private MeshNodeCardControl GetCardControl(MeshNode node) =>
        MeshNodeCardControl.FromNode(node, node.Path, BoundItemArea, BoundDisableNavigation);

    private void ToggleSearchOptions()
    {
        _showSearchOptions = !_showSearchOptions;
        if (_showSearchOptions)
        {
            _editableHiddenQuery = BoundHiddenQuery;
        }
    }

    private async Task ApplySearchOptions()
    {
        _overriddenHiddenQuery = _editableHiddenQuery;
        _showSearchOptions = false;
        await LoadResultsAsync();
        UpdateSearchUrl();
    }

    public void Dispose()
    {
        _reactiveSubscription?.Dispose();
    }
}
