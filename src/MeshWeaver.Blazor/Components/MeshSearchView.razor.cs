using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


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
    private HashSet<string> _expandedRowGroups = new();
    private string _lastBoundVisibleQuery = "";
    private string _lastBoundHiddenQuery = "";
    private bool _showSearchOptions;
    private string _editableHiddenQuery = "";
    private string? _overriddenHiddenQuery;

    // ----- Delete affordance + keyboard navigation state -----
    // Per-node delete permission (canonical hub.CheckPermission(path, Permission.Delete) snapshot).
    // Absent / false ⇒ no trash affordance for that path (fail-closed).
    private readonly Dictionary<string, bool> _canDeleteByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _permissionRequested =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _affordanceSubscriptions = new();
    // The card whose trash is armed for confirmation (two-step delete; null = none armed).
    private string? _pendingDeletePath;
    // The keyboard-highlighted result path (Arrow Up/Down). Distinct from the SelectedPath param.
    private string? _keyboardSelectedPath;

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
    private MeshSearchRenderMode BoundRenderMode
    {
        get
        {
            switch (ViewModel?.RenderMode)
            {
                case MeshSearchRenderMode mode:
                    return mode;
                case string s when Enum.TryParse<MeshSearchRenderMode>(s, true, out var parsed):
                    return parsed;
                // Controls round-trip through the synchronization stream as JSON;
                // enums serialize as strings (EnumMemberJsonStringEnumConverter).
                case JsonElement { ValueKind: JsonValueKind.String } je
                    when Enum.TryParse<MeshSearchRenderMode>(je.GetString(), true, out var fromJson):
                    return fromJson;
                case JsonElement { ValueKind: JsonValueKind.Number } jn
                    when jn.TryGetInt32(out var n) && Enum.IsDefined(typeof(MeshSearchRenderMode), n):
                    return (MeshSearchRenderMode)n;
                default:
                    return MeshSearchRenderMode.Grouped;
            }
        }
    }

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

    private int? BoundMaxRows => BoundSections?.MaxRows;

    /// <summary>
    /// Maximum visible items per group based on MaxRows * MaxColumns.
    /// Returns null if no row limit is set or the group is expanded.
    /// </summary>
    private int? GetMaxVisibleItems(string groupKey)
    {
        var maxRows = BoundMaxRows;
        if (!maxRows.HasValue || maxRows.Value <= 0 || _expandedRowGroups.Contains(groupKey))
            return null;
        var cols = BoundMaxColumns ?? 3;
        return maxRows.Value * cols;
    }

    private void ToggleGroupExpanded(string groupKey)
    {
        if (_expandedRowGroups.Contains(groupKey))
            _expandedRowGroups.Remove(groupKey);
        else
            _expandedRowGroups.Add(groupKey);
        StateHasChanged();
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
                if (IsNamespaceTreeMode)
                    LoadTreeSearch();
                else if (IsGraphNavigatorMode)
                    RunGraphNavigatorSearch(); // browse when empty, vector search when text typed
                else
                    LoadResults();
            }
        }

        if (_initialized && BoundHiddenQuery != _lastBoundHiddenQuery)
        {
            _lastBoundHiddenQuery = BoundHiddenQuery;
            if (!IsPrecomputedMode)
            {
                if (IsNamespaceTreeMode)
                    ResetTree();
                else if (IsGraphNavigatorMode)
                    ResetGraphNavigator(); // re-rooted → recompute above + below
                else
                    LoadResults();
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

            // Namespace tree mode: lazily load the first level only.
            if (IsNamespaceTreeMode)
            {
                InitializeTree();
                return;
            }

            // Graph navigator: load the next level below + ancestors above. When the
            // box already carries search text (e.g. ?q=), also run the semantic query
            // so the initial render shows results instead of the browse view.
            if (IsGraphNavigatorMode)
            {
                InitializeGraphNavigator();
                if (!string.IsNullOrWhiteSpace(_currentValue))
                    LoadResults();
                return;
            }

            // Reactive mode: subscribe to Query for live updates
            if (BoundReactiveMode)
            {
                SubscribeToReactiveUpdates();
                return;
            }

            // Client-side query mode (one-shot)
            LoadResults();
            StateHasChanged();
        }
    }

    private Task OnValueChanged(string value)
    {
        _currentValue = value;
        if (BoundLiveSearch && !IsPrecomputedMode)
        {
            if (IsNamespaceTreeMode)
                LoadTreeSearch();
            else if (IsGraphNavigatorMode)
                RunGraphNavigatorSearch();
            else
                LoadResults();
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
            if (IsNamespaceTreeMode)
                LoadTreeSearch();
            else if (IsGraphNavigatorMode)
                RunGraphNavigatorSearch();
            else
                LoadResults();
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

    private void LoadResults()
    {
        _isLoading = true;
        StateHasChanged();

        var query = BuildFullQuery();
        // Subscribe to Query so the result set stays live as data changes.
        // _reactiveSubscription holds the active subscription (set up in SubscribeToReactiveUpdates).
        _reactiveSubscription?.Dispose();
        _reactiveSubscription = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Subscribe(
                change =>
                {
                    var current = (IReadOnlyList<MeshNode>)_nodes;
                    var merged = change.ChangeType switch
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

                    if (BoundExcludeBasePath && !string.IsNullOrEmpty(BoundNamespace))
                    {
                        var basePath = BoundNamespace.Trim('/');
                        merged = merged.Where(n => n.Path != basePath).ToList();
                    }

                    _nodes = merged.ToList();
                    ResolveDeletePermissions(_nodes);

                    if (ViewModel != null)
                    {
                        _computedGroups = ProcessResults(_nodes);
                        InitializeCollapsedState(_computedGroups);
                    }
                    _isLoading = false;
                    InvokeAsync(StateHasChanged);
                },
                ex =>
                {
                    var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()
                        ?.CreateLogger("MeshWeaver.MeshSearchView");
                    logger?.LogWarning(ex, "MeshSearchView query failed: {Query}", BuildFullQuery());
                    _nodes = new List<MeshNode>();
                    _computedGroups = null;
                    _isLoading = false;
                    InvokeAsync(StateHasChanged);
                });
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

        _reactiveSubscription = MeshQuery.Query<MeshNode>(request)
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
                        // Reset the list — dedupe by path in case the server's Initial
                        // payload itself contains duplicates (UNION ALL across partitions
                        // on the server side can surface the same path twice).
                        var seen = new HashSet<string>();
                        _nodes = new List<MeshNode>();
                        foreach (var n in change.Items)
                        {
                            if (n.Path != null && seen.Add(n.Path))
                                _nodes.Add(n);
                        }
                    }
                    else if (change.ChangeType == QueryChangeType.Added)
                    {
                        // Only add items whose path isn't already present — otherwise a
                        // reactive Added event that overlaps the current set doubles rows.
                        var existing = new HashSet<string>(_nodes.Where(n => n.Path != null).Select(n => n.Path!));
                        foreach (var n in change.Items)
                        {
                            if (n.Path != null && existing.Add(n.Path))
                                _nodes.Add(n);
                        }
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

                    ResolveDeletePermissions(_nodes);
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
            // When grouping by Category, fall back to NodeType for nodes that don't
            // carry an explicit category so they still bucket meaningfully rather
            // than collapsing into a single empty-label group.
            .GroupBy(n =>
            {
                var val = GetPropertyValue(n, groupByProperty);
                if (!string.IsNullOrEmpty(val)) return val;
                if (groupByProperty.Equals("Category", StringComparison.OrdinalIgnoreCase))
                    return n.NodeType?.Split('/').LastOrDefault() ?? "";
                return "";
            })
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

    // No hardcoded property list: resolve {property} generically — first against the MeshNode's own
    // properties by name (case-insensitive, so "namespace"/"Namespace" both work), then, if the node
    // has no such property (or it is null), against the node's Content (e.g. group LanguageModels by
    // their content "provider"). Works for any field; PascalCase/camelCase agnostic.
    private string? GetPropertyValue(MeshNode node, string property)
    {
        if (string.IsNullOrEmpty(property)) return null;
        var prop = typeof(MeshNode).GetProperty(property,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);
        if (prop != null && prop.GetIndexParameters().Length == 0 && prop.GetValue(node) is { } v)
            return v.ToString();
        return GetContentProperty(node.Content, property);
    }

    private string? GetContentProperty(object? content, string property)
    {
        if (content == null || string.IsNullOrEmpty(property)) return null;

        if (content is JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Object) return null;
            if (json.TryGetProperty(property, out var prop))
                return GetJsonValue(prop);

            var camelCase = char.ToLowerInvariant(property[0]) + property.Substring(1);
            if (json.TryGetProperty(camelCase, out var camelProp))
                return GetJsonValue(camelProp);

            var pascalCase = char.ToUpperInvariant(property[0]) + property.Substring(1);
            if (json.TryGetProperty(pascalCase, out var pascalProp))
                return GetJsonValue(pascalProp);

            return null;
        }

        // Typed content record (e.g. ModelDefinition.Provider) — reflect by name, case-insensitive.
        var cp = content.GetType().GetProperty(property,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);
        return cp != null && cp.GetIndexParameters().Length == 0 ? cp.GetValue(content)?.ToString() : null;
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

    private const int CompletionLimit = 20;

    // Higher score = better. Sort descending.
    private static readonly IComparer<QuerySuggestion> CompletionByScore =
        Comparer<QuerySuggestion>.Create((a, b) => b.Score.CompareTo(a.Score));

    /// <summary>
    /// Returns a stream of top-N completion snapshots for <paramref name="query"/>.
    /// Monaco subscribes per query and pushes each fresh snapshot to the suggest widget
    /// as it arrives. No Task, no await, no component-level state.
    /// </summary>
    private IObservable<IReadOnlyList<CompletionItem>> GetCompletions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        // Parse the query to split into basePath and prefix for AutocompleteAsync
        var text = query.TrimStart('@');
        string basePath;
        string namePrefix;

        if (text.EndsWith("/"))
        {
            basePath = text.TrimEnd('/');
            namePrefix = "";
        }
        else
        {
            var lastSlash = text.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                basePath = text[..lastSlash];
                namePrefix = text[(lastSlash + 1)..];
            }
            else
            {
                basePath = BoundNamespace ?? "";
                namePrefix = text;
            }
        }

        return MeshQuery
            .Autocomplete(basePath, namePrefix, AutocompleteMode.RelevanceFirst, CompletionLimit, BoundNamespace)
            .Select(snapshot => (IReadOnlyList<CompletionItem>)snapshot
                .Select(s => new CompletionItem
                {
                    Label = s.Name ?? s.Path,
                    InsertText = $"@{s.Path}/",
                    Description = s.NodeType ?? "",
                    Path = s.Path,
                    Category = s.NodeType ?? "Nodes",
                    IconUrl = s.Icon,
                    SortKey = (99999 - Math.Clamp((int)s.Score, 0, 99999)).ToString("D5")
                })
                .ToArray());
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

    /// <summary>
    /// The re-rooted "Drill down" area (e.g. "Search"). When non-empty, each card
    /// and folder shows a secondary anchor to <c>/{path}/{BoundDrillDownArea}</c>;
    /// the primary click still opens the node's default page <c>/{path}</c>.
    /// Empty/unset = no drill-down affordance (opt-in).
    /// </summary>
    private string? BoundDrillDownArea
    {
        get
        {
            if (ViewModel?.DrillDownArea is string s) return string.IsNullOrWhiteSpace(s) ? null : s;
            if (ViewModel?.DrillDownArea is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                var v = je.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
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

    private string CardGridStyle
    {
        get
        {
            var maxCols = BoundMaxColumns;
            if (!maxCols.HasValue || maxCols.Value <= 0) return "";
            if (maxCols.Value == 1) return "grid-template-columns: 1fr;";
            // Container-responsive: auto-fill capped at maxCols via percentage minimum
            var pct = 100.0 / maxCols.Value;
            return $"grid-template-columns: repeat(auto-fill, minmax(max({pct:F1}% - 8px, 200px), 1fr));";
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

    #region Namespace tree mode (MeshSearchRenderMode.NamespaceTree)

    /// <summary>One lazily-loaded namespace level: direct children resolved into folders/leaves.</summary>
    private sealed record TreeLevel(bool IsLoading, ImmutableList<NamespaceTreeItem> Items)
    {
        public static readonly TreeLevel Loading = new(true, ImmutableList<NamespaceTreeItem>.Empty);
    }

    /// <summary>Direct-children count probes cap out here; the badge shows "99+" at the cap.</summary>
    private const int FolderCountProbeCap = 100;

    private ImmutableDictionary<string, TreeLevel> _treeLevels =
        ImmutableDictionary<string, TreeLevel>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private ImmutableHashSet<string> _expandedFolders =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<string, IDisposable> _treeLevelSubscriptions =
        ImmutableDictionary<string, IDisposable>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    private ImmutableList<IDisposable> _treeProbeSubscriptions = ImmutableList<IDisposable>.Empty;
    private IDisposable? _treeSearchSubscription;
    private ImmutableList<NamespaceTreeItem>? _treeSearchItems;
    private bool _treeSearchLoading;

    private bool IsNamespaceTreeMode =>
        BoundRenderMode == MeshSearchRenderMode.NamespaceTree && !IsPrecomputedMode;

    private bool TreeHasSearchText => !string.IsNullOrWhiteSpace(_currentValue);

    /// <summary>The catalog root — the namespace: of the hidden query (fallback: Namespace property).</summary>
    private string TreeRootNamespace
    {
        get
        {
            var match = Regex.Match(BoundHiddenQuery, @"(?:^|\s)namespace:(\S+)");
            if (match.Success)
                return match.Groups[1].Value.Trim('/');
            return BoundNamespace.Trim('/');
        }
    }

    private ILogger? TreeLogger =>
        Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.MeshSearchView");

    /// <summary>
    /// Per-level query: the hidden query's filters with namespace: forced to
    /// <paramref name="ns"/> and any scope: stripped (levels are always direct children).
    /// </summary>
    private string BuildTreeLevelQuery(string ns)
    {
        var query = Regex.Replace(BoundHiddenQuery, @"(?:^|\s)scope:\S+", " ");
        query = Regex.IsMatch(query, @"(?:^|\s)namespace:\S+")
            ? Regex.Replace(query, @"(?:^|\s)namespace:\S+", $" namespace:{ns}")
            : $"namespace:{ns} {query}";
        return Regex.Replace(query, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Subtree search query for typed text: same filters, namespace: at the root,
    /// scope:descendants, plus the user's search terms.
    /// </summary>
    private string BuildTreeSearchQuery()
        => Regex.Replace($"{BuildTreeLevelQuery(TreeRootNamespace)} scope:descendants {_currentValue.Trim()}", @"\s+", " ").Trim();

    private void InitializeTree()
    {
        LoadTreeLevel(TreeRootNamespace);
        if (TreeHasSearchText)
            LoadTreeSearch();
    }

    /// <summary>Disposes all tree subscriptions and reloads from the (possibly changed) root.</summary>
    private void ResetTree()
    {
        DisposeTreeSubscriptions();
        _treeLevels = _treeLevels.Clear();
        _expandedFolders = _expandedFolders.Clear();
        _treeSearchItems = null;
        InitializeTree();
    }

    private void DisposeTreeSubscriptions()
    {
        foreach (var subscription in _treeLevelSubscriptions.Values)
            subscription.Dispose();
        _treeLevelSubscriptions = _treeLevelSubscriptions.Clear();
        foreach (var probe in _treeProbeSubscriptions)
            probe.Dispose();
        _treeProbeSubscriptions = ImmutableList<IDisposable>.Empty;
        _treeSearchSubscription?.Dispose();
        _treeSearchSubscription = null;
    }

    /// <summary>
    /// Subscribes the live direct-children query for one namespace level. Each
    /// structural emission re-probes child counts and rebuilds the level's items.
    /// </summary>
    private void LoadTreeLevel(string ns)
    {
        if (_treeLevelSubscriptions.TryGetValue(ns, out var existing))
            existing.Dispose();

        _treeLevels = _treeLevels.SetItem(ns, TreeLevel.Loading);

        // Live per-emission snapshot of the level's direct children, keyed by path.
        var levelNodes = ImmutableDictionary<string, MeshNode>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);

        var subscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(BuildTreeLevelQuery(ns)))
            .Subscribe(
                change =>
                {
                    switch (change.ChangeType)
                    {
                        case QueryChangeType.Initial:
                        case QueryChangeType.Reset:
                            levelNodes = change.Items
                                .Where(n => !string.IsNullOrEmpty(n.Path))
                                .Aggregate(
                                    ImmutableDictionary<string, MeshNode>.Empty
                                        .WithComparers(StringComparer.OrdinalIgnoreCase),
                                    (map, n) => map.SetItem(n.Path, n));
                            break;
                        case QueryChangeType.Added:
                        case QueryChangeType.Updated:
                            foreach (var n in change.Items.Where(n => !string.IsNullOrEmpty(n.Path)))
                                levelNodes = levelNodes.SetItem(n.Path, n);
                            break;
                        case QueryChangeType.Removed:
                            foreach (var n in change.Items.Where(n => !string.IsNullOrEmpty(n.Path)))
                                levelNodes = levelNodes.Remove(n.Path);
                            break;
                        default:
                            return;
                    }
                    ProbeTreeChildCounts(ns, levelNodes.Values.ToImmutableList());
                },
                ex =>
                {
                    TreeLogger?.LogWarning(ex, "Namespace tree level query failed for {Namespace}", ns);
                    InvokeAsync(() =>
                    {
                        _treeLevels = _treeLevels.SetItem(
                            ns, new TreeLevel(false, ImmutableList<NamespaceTreeItem>.Empty));
                        StateHasChanged();
                    });
                });

        _treeLevelSubscriptions = _treeLevelSubscriptions.SetItem(ns, subscription);
    }

    /// <summary>
    /// Fires one direct-children existence/count probe per child node and resolves
    /// the level once all probes answered. Subscribe-all-upfront (CombineLatest)
    /// per AsynchronousCalls.md; each probe Catches to a 0-count sentinel so one
    /// failing probe degrades a single folder to a leaf instead of wedging the level.
    /// </summary>
    private void ProbeTreeChildCounts(string ns, ImmutableList<MeshNode> nodes)
    {
        if (nodes.Count == 0)
        {
            InvokeAsync(() =>
            {
                _treeLevels = _treeLevels.SetItem(
                    ns, new TreeLevel(false, ImmutableList<NamespaceTreeItem>.Empty));
                StateHasChanged();
            });
            return;
        }

        var probes = nodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .Select(n =>
            {
                var path = n.Path;
                return MeshQuery
                    .Query<MeshNode>(MeshQueryRequest.FromQuery(
                        $"{BuildTreeLevelQuery(path)} limit:{FolderCountProbeCap}"))
                    .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    .Take(1)
                    .Select(c => (Path: path, Count: c.Items.Count))
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Catch((Exception ex) =>
                    {
                        TreeLogger?.LogWarning(ex, "Child-count probe failed for {Path}", path);
                        return Observable.Return((Path: path, Count: 0));
                    });
            })
            .ToArray();

        var probeSubscription = Observable.CombineLatest(probes)
            .Take(1)
            .Subscribe(counts => InvokeAsync(() =>
            {
                var countMap = counts.ToImmutableDictionary(
                    c => c.Path, c => c.Count, StringComparer.OrdinalIgnoreCase);
                ResolveDeletePermissions(nodes);
                _treeLevels = _treeLevels.SetItem(
                    ns, new TreeLevel(false, NamespaceTreeBuilder.BuildLevel(ns, nodes, countMap)));
                StateHasChanged();
            }));
        _treeProbeSubscriptions = _treeProbeSubscriptions.Add(probeSubscription);
    }

    private void OnTreeFolderHeaderClick(string folderPath, bool lazy)
    {
        if (lazy)
            ToggleTreeFolder(folderPath);
        else
            ToggleSearchTreeFolder(folderPath);
    }

    private void ToggleTreeFolder(string folderPath)
    {
        if (_expandedFolders.Contains(folderPath))
        {
            _expandedFolders = _expandedFolders.Remove(folderPath);
        }
        else
        {
            _expandedFolders = _expandedFolders.Add(folderPath);
            if (!_treeLevels.ContainsKey(folderPath))
                LoadTreeLevel(folderPath);
        }
        StateHasChanged();
    }

    /// <summary>
    /// Typed-text mode: one live subtree query; results are relativised to the root
    /// and grouped into nested namespace sections via <see cref="NamespaceTreeBuilder.Build"/>.
    /// Clearing the text returns to the lazily-loaded browse levels (still cached).
    /// </summary>
    private void LoadTreeSearch()
    {
        _treeSearchSubscription?.Dispose();
        _treeSearchSubscription = null;

        if (!TreeHasSearchText)
        {
            _treeSearchItems = null;
            _treeSearchLoading = false;
            StateHasChanged();
            return;
        }

        _treeSearchLoading = true;
        StateHasChanged();

        var root = TreeRootNamespace;
        var resultNodes = ImmutableDictionary<string, MeshNode>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);

        _treeSearchSubscription = MeshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(BuildTreeSearchQuery()))
            .Subscribe(
                change =>
                {
                    switch (change.ChangeType)
                    {
                        case QueryChangeType.Initial:
                        case QueryChangeType.Reset:
                            resultNodes = change.Items
                                .Where(n => !string.IsNullOrEmpty(n.Path))
                                .Aggregate(
                                    ImmutableDictionary<string, MeshNode>.Empty
                                        .WithComparers(StringComparer.OrdinalIgnoreCase),
                                    (map, n) => map.SetItem(n.Path, n));
                            break;
                        case QueryChangeType.Added:
                        case QueryChangeType.Updated:
                            foreach (var n in change.Items.Where(n => !string.IsNullOrEmpty(n.Path)))
                                resultNodes = resultNodes.SetItem(n.Path, n);
                            break;
                        case QueryChangeType.Removed:
                            foreach (var n in change.Items.Where(n => !string.IsNullOrEmpty(n.Path)))
                                resultNodes = resultNodes.Remove(n.Path);
                            break;
                        default:
                            return;
                    }
                    var items = NamespaceTreeBuilder.Build(root, resultNodes.Values.ToImmutableList());
                    ResolveDeletePermissions(resultNodes.Values);
                    InvokeAsync(() =>
                    {
                        _treeSearchItems = items;
                        _treeSearchLoading = false;
                        StateHasChanged();
                    });
                },
                ex =>
                {
                    TreeLogger?.LogWarning(ex, "Namespace tree search failed: {Query}", BuildTreeSearchQuery());
                    InvokeAsync(() =>
                    {
                        _treeSearchItems = ImmutableList<NamespaceTreeItem>.Empty;
                        _treeSearchLoading = false;
                        StateHasChanged();
                    });
                });
    }

    private static string FormatTreeCount(int count)
        => count >= FolderCountProbeCap ? $"{FolderCountProbeCap - 1}+" : count.ToString();

    /// <summary>Search-mode folders default to expanded; the toggle reuses _collapsedGroups.</summary>
    private bool IsSearchTreeFolderExpanded(string folderPath)
        => !_collapsedGroups.Contains($"tree:{folderPath}");

    private void ToggleSearchTreeFolder(string folderPath)
        => ToggleGroup($"tree:{folderPath}");

    private bool TreeRootIsLoading =>
        !_treeLevels.TryGetValue(TreeRootNamespace, out var level) || level.IsLoading;

    private bool TreeRootIsEmpty =>
        _treeLevels.TryGetValue(TreeRootNamespace, out var level)
        && !level.IsLoading
        && level.Items.Count == 0;

    #endregion

    #region Graph navigator mode (MeshSearchRenderMode.GraphNavigator)

    private GraphNavigatorModel? _navModel;
    private bool _navLoading;
    private bool _includeDocuments;
    private IDisposable? _navSubscription;

    private bool IsGraphNavigatorMode =>
        BoundRenderMode == MeshSearchRenderMode.GraphNavigator && !IsPrecomputedMode;

    /// <summary>The node the navigator is centered on — the hidden query's namespace: (fallback: Namespace).</summary>
    private string NavRootPath => TreeRootNamespace;

    /// <summary>Re-root target area on click — keeps the navigator. Falls back to the Search area.</summary>
    private string NavArea => string.IsNullOrEmpty(BoundDrillDownArea)
        ? MeshNodeLayoutAreas.SearchArea
        : BoundDrillDownArea!;

    /// <summary>
    /// A pure sub-namespace (<see cref="GraphNavNamespace"/>) has NO node of its own, so it cannot
    /// host a node-page Search area: routing to <c>/{nsPath}/Search</c> would render a layout area on
    /// a phantom node and NotFound-storm its hub. Instead, a namespace click redirects to the global
    /// search control (<c>/search</c>) scoped to that namespace — carrying the navigator's own hidden
    /// query (filters + the "Include documents" toggle state via <see cref="BuildNavBelowQuery"/>) so
    /// the search shows exactly what the navigator would have if it could re-root there.
    /// </summary>
    private string BuildNamespaceSearchHref(string nsPath) =>
        $"/search?ns={Uri.EscapeDataString(nsPath)}&hq={Uri.EscapeDataString(BuildNavBelowQuery(nsPath))}";

    /// <summary>
    /// Below = the current node's whole subtree (one <c>scope:descendants</c> query — no N+1 probes).
    /// The builder surfaces the immediate level from it: real nodes here (cards on top) + pure
    /// sub-namespaces (drill links at the bottom). Documents (indexed content) are excluded unless
    /// the user ticks "Include documents" — and ONLY here, in the node Search area; top-level/global
    /// search keeps documents. When included, Document nodes (<c>{collection}/_Documents/{slug}</c>)
    /// join the subtree, so content shows up as a navigable namespace too.
    /// </summary>
    private string BuildNavBelowQuery(string root)
    {
        var q = $"{BuildTreeLevelQuery(root)} scope:descendants";
        if (!_includeDocuments)
            q += " -nodeType:Document";
        return Regex.Replace(q, @"\s+", " ").Trim();
    }

    /// <summary>Above = the ancestor chain INCLUDING self, so the builder can pull out the current node
    /// and order the rail. Real ancestors only — empty namespace segments are never nodes.</summary>
    private static string BuildNavAboveQuery(string root) =>
        $"path:{root} scope:ancestorsandself is:main";

    /// <summary>
    /// The navigator browses the graph when the box is empty; a non-empty box switches to a
    /// real subtree query (see <see cref="RunGraphNavigatorSearch"/>) so typed text flows
    /// through the standard MeshQuery.Query surface and its bare-text tokens hit the Postgres
    /// HNSW vector intercept. The navigator level is therefore always shown unfiltered — there
    /// is no client-side narrowing here anymore.
    /// </summary>
    private bool NavBrowsing => IsGraphNavigatorMode && string.IsNullOrWhiteSpace(_currentValue);

    /// <summary>Mesh nodes at the current level (rendered only in browse mode).</summary>
    private IReadOnlyList<GraphNavNode> NavNodes => _navModel?.Nodes ?? ImmutableList<GraphNavNode>.Empty;

    /// <summary>Sub-namespace drill links at the current level (rendered only in browse mode).</summary>
    private IReadOnlyList<GraphNavNamespace> NavNamespaces => _navModel?.Namespaces ?? ImmutableList<GraphNavNamespace>.Empty;

    /// <summary>
    /// GraphNavigator search: browse the graph when the box is empty, run a real query when the
    /// user types. Routing typed text through <see cref="LoadResults"/> (MeshQuery.Query) means a
    /// query like <c>namespace:{node} scope:subtree … laptop</c> reaches
    /// <c>PostgreSqlMeshQuery.QueryAsync</c>'s vector intercept — semantic HNSW cosine search over
    /// the node's subtree — exactly like <see cref="LoadTreeSearch"/> does for NamespaceTree mode.
    /// Clearing the box drops the query and returns to the (still-loaded) navigator browse view.
    /// </summary>
    private void RunGraphNavigatorSearch()
    {
        if (string.IsNullOrWhiteSpace(_currentValue))
        {
            _reactiveSubscription?.Dispose();
            _reactiveSubscription = null;
            _isLoading = false;
            StateHasChanged();
        }
        else
        {
            LoadResults();
        }
    }

    private void ToggleIncludeDocuments(ChangeEventArgs e)
    {
        var include = e.Value is bool b ? b : bool.TryParse(e.Value?.ToString(), out var parsed) && parsed;
        if (_includeDocuments == include) return;
        _includeDocuments = include;
        ResetGraphNavigator();
    }

    private void InitializeGraphNavigator()
    {
        _navSubscription?.Dispose();
        _navLoading = true;
        StateHasChanged();

        var root = NavRootPath;

        // hub.GetQuery is the canonical synced-query surface (delegates to workspace → the shared
        // IMeshNodeStreamCache): live, deduped, all-Initial gated, provider-fanned, injects hub
        // JsonSerializerOptions. The below id keys on the doc toggle so each variant caches separately.
        var below = Hub.GetQuery($"nav-below:{root}:{_includeDocuments}", BuildNavBelowQuery(root));
        var above = string.IsNullOrEmpty(root)
            ? Observable.Return<IEnumerable<MeshNode>>(Array.Empty<MeshNode>())
            : Hub.GetQuery($"nav-above:{root}", BuildNavAboveQuery(root));

        _navSubscription = below
            .CombineLatest(above, (b, a) => (Below: b, Above: a))
            .Subscribe(
                t =>
                {
                    var aboveList = (t.Above ?? Enumerable.Empty<MeshNode>()).ToList();
                    var belowList = (t.Below ?? Enumerable.Empty<MeshNode>()).ToList();
                    var current = aboveList.FirstOrDefault(n =>
                        string.Equals(n.Path?.Trim('/'), root, StringComparison.OrdinalIgnoreCase));
                    var model = GraphNavigatorBuilder.Build(root, aboveList, belowList, current);
                    InvokeAsync(() =>
                    {
                        _navModel = model;
                        _navLoading = false;
                        StateHasChanged();
                    });
                },
                ex =>
                {
                    TreeLogger?.LogWarning(ex, "Graph navigator query failed for {Root}", root);
                    InvokeAsync(() =>
                    {
                        _navModel = GraphNavigatorBuilder.Build(
                            root, Array.Empty<MeshNode>(), Array.Empty<MeshNode>());
                        _navLoading = false;
                        StateHasChanged();
                    });
                });
    }

    private void ResetGraphNavigator()
    {
        _navSubscription?.Dispose();
        _navSubscription = null;
        _navModel = null;
        InitializeGraphNavigator();
    }

    /// <summary>Current node display name — the node when present, else the last path segment.</summary>
    private string NavCurrentName
    {
        get
        {
            var current = _navModel?.Current;
            if (!string.IsNullOrEmpty(current?.Name)) return current!.Name!;
            var root = NavRootPath;
            if (string.IsNullOrEmpty(root)) return "Mesh";
            var slash = root.LastIndexOf('/');
            return slash < 0 ? root : root[(slash + 1)..];
        }
    }

    #endregion

    #region Delete affordance + keyboard navigation

    /// <summary>True when the trash affordance may be shown for <paramref name="path"/>.</summary>
    private bool CanDelete(string? path) =>
        !string.IsNullOrEmpty(path)
        && _canDeleteByPath.TryGetValue(path, out var ok)
        && ok;

    /// <summary>
    /// Resolves the per-node delete permission for the given result nodes via the canonical
    /// <c>hub.CheckPermission(path, Permission.Delete)</c> surface (the same gate
    /// <see cref="MeshWeaver.Graph.DeleteLayoutArea"/> uses). Bounded snapshot — <c>Take(1)</c>
    /// + <c>Timeout</c>, fail-closed to "no delete" so a stuck lookup never offers a trash we
    /// cannot honour. Idempotent: each path is probed at most once per component instance.
    /// Skipped entirely on a picker selection surface (no manage affordance there).
    /// </summary>
    private void ResolveDeletePermissions(IEnumerable<MeshNode> nodes)
    {
        if (OnNodeSelected.HasDelegate)
            return;

        foreach (var node in nodes)
        {
            var path = node.Path;
            if (string.IsNullOrEmpty(path) || !_permissionRequested.Add(path))
                continue;

            var capturedPath = path;
            var sub = Hub.CheckPermission(capturedPath, Permission.Delete)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .Catch((Exception _) => Observable.Return(false))
                .Subscribe(canDelete => InvokeAsync(() =>
                {
                    _canDeleteByPath[capturedPath] = canDelete;
                    StateHasChanged();
                }));
            _affordanceSubscriptions.Add(sub);
        }
    }

    /// <summary>First click on the trash arms the inline confirm for that card.</summary>
    private void RequestDelete(MeshNode node)
    {
        if (!CanDelete(node.Path))
            return;
        _pendingDeletePath = node.Path;
        StateHasChanged();
    }

    /// <summary>Dismisses the armed confirm without deleting.</summary>
    private void CancelDelete()
    {
        _pendingDeletePath = null;
        StateHasChanged();
    }

    /// <summary>
    /// Confirms the delete and routes it through the framework (<c>IMeshService.DeleteNode</c>).
    /// The live result subscription emits a <c>Removed</c> change and the card drops out of the
    /// grid on its own — no manual reload. Cold observable, so we Subscribe (and surface errors).
    /// </summary>
    private void ConfirmDelete(MeshNode node)
    {
        var path = node.Path;
        _pendingDeletePath = null;
        if (string.IsNullOrEmpty(path) || !CanDelete(path))
        {
            StateHasChanged();
            return;
        }

        _affordanceSubscriptions.Add(MeshQuery.DeleteNode(path).Subscribe(
            _ => { },
            ex =>
            {
                var logger = Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger("MeshWeaver.MeshSearchView");
                logger?.LogWarning(ex, "MeshSearchView delete failed: {Path}", path);
            }));

        if (string.Equals(_keyboardSelectedPath, path, StringComparison.OrdinalIgnoreCase))
            _keyboardSelectedPath = null;
        StateHasChanged();
    }

    /// <summary>
    /// The flat, in-render-order list of result cards the keyboard navigates — visible items of
    /// the non-collapsed groups, respecting each group's row cap. Empty in tree / navigator /
    /// picker modes (their browse surfaces are not keyboard-list navigable), so the handler no-ops.
    /// </summary>
    private List<MeshNode> GetNavigableNodes()
    {
        var result = new List<MeshNode>();
        if (IsNamespaceTreeMode || IsGraphNavigatorMode || OnNodeSelected.HasDelegate)
            return result;

        foreach (var group in GetGroups())
        {
            if (_collapsedGroups.Contains(group.GroupKey))
                continue;
            var maxVisible = GetMaxVisibleItems(group.GroupKey);
            IEnumerable<object> items = maxVisible.HasValue
                ? group.Items.Take(maxVisible.Value)
                : group.Items;
            foreach (var item in items)
                if (item is MeshNode node)
                    result.Add(node);
        }
        return result;
    }

    /// <summary>preventDefault on the result list only where keyboard navigation is live, so the
    /// browse modes (tree / navigator) and the picker keep their native key handling.</summary>
    private bool ResultsPreventDefault =>
        !IsNamespaceTreeMode && !IsGraphNavigatorMode && !OnNodeSelected.HasDelegate;

    /// <summary>
    /// Desktop keyboard interaction on the result list: ↑/↓ move the highlight (wrapping), Enter
    /// opens the highlighted card (or confirms an armed delete), Delete/Backspace arm the gated
    /// delete on the highlighted card, Escape cancels an armed delete or clears the highlight.
    /// </summary>
    private void OnResultsKeyDown(KeyboardEventArgs e)
    {
        var nav = GetNavigableNodes();
        if (nav.Count == 0)
            return;

        var currentIndex = _keyboardSelectedPath is null
            ? -1
            : nav.FindIndex(n => string.Equals(n.Path, _keyboardSelectedPath, StringComparison.OrdinalIgnoreCase));

        switch (e.Key)
        {
            case "ArrowDown":
                _keyboardSelectedPath = nav[currentIndex < 0 ? 0 : (currentIndex + 1) % nav.Count].Path;
                _pendingDeletePath = null;
                StateHasChanged();
                break;
            case "ArrowUp":
                _keyboardSelectedPath = nav[currentIndex < 0 ? nav.Count - 1 : (currentIndex - 1 + nav.Count) % nav.Count].Path;
                _pendingDeletePath = null;
                StateHasChanged();
                break;
            case "Enter":
                if (_pendingDeletePath is not null)
                {
                    var pending = nav.FirstOrDefault(n =>
                        string.Equals(n.Path, _pendingDeletePath, StringComparison.OrdinalIgnoreCase));
                    if (pending is not null)
                        ConfirmDelete(pending);
                }
                else if (currentIndex >= 0)
                {
                    OpenNode(nav[currentIndex]);
                }
                break;
            case "Delete":
            case "Backspace":
                if (currentIndex >= 0 && CanDelete(nav[currentIndex].Path))
                {
                    _pendingDeletePath = nav[currentIndex].Path;
                    StateHasChanged();
                }
                break;
            case "Escape":
                if (_pendingDeletePath is not null)
                    _pendingDeletePath = null;
                else
                    _keyboardSelectedPath = null;
                StateHasChanged();
                break;
        }
    }

    /// <summary>Opens a result card the same way a primary click would: select (picker), post the
    /// click message (ClickMessageAddress), or navigate to the node's page.</summary>
    private void OpenNode(MeshNode node)
    {
        if (OnNodeSelected.HasDelegate)
        {
            OnNodeSelected.InvokeAsync(node);
            return;
        }
        var clickAddress = ViewModel?.ClickMessageAddress?.ToString();
        if (!string.IsNullOrEmpty(clickAddress))
        {
            PostClickMessage(node, clickAddress);
            return;
        }
        if (!string.IsNullOrEmpty(node.Path))
            Navigation.NavigateTo($"/{node.Path}");
    }

    #endregion

    private void ToggleSearchOptions()
    {
        _showSearchOptions = !_showSearchOptions;
        if (_showSearchOptions)
        {
            _editableHiddenQuery = BoundHiddenQuery;
        }
    }

    private Task ApplySearchOptions()
    {
        _overriddenHiddenQuery = _editableHiddenQuery;
        _showSearchOptions = false;
        if (IsNamespaceTreeMode)
            ResetTree();
        else
            LoadResults();
        UpdateSearchUrl();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _reactiveSubscription?.Dispose();
        DisposeTreeSubscriptions();
        _navSubscription?.Dispose();
        foreach (var sub in _affordanceSubscriptions)
            sub.Dispose();
        _affordanceSubscriptions.Clear();
    }
}
