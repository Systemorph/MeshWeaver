// Suppress ASP0006 - sequence numbers in dynamic RenderFragment loops are unavoidable for list rendering
#pragma warning disable ASP0006

using System.Text.Json;
using System.Reactive.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.FluentUI.AspNetCore.Components;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Renders MeshSearchControl. Supports two modes:
/// 1. PrecomputedGroups (new) - server-side grouping, just renders results
/// 2. HiddenQuery/VisibleQuery (legacy) - client-side query via IMeshQuery
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

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private IMeshQuery MeshQuery { get; set; } = default!;

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
    private string BoundHiddenQuery => ViewModel?.HiddenQuery?.ToString() ?? "";
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
    private bool BoundExcludeBasePath => ViewModel?.ExcludeBasePath is bool exclude ? exclude : true;
    private bool BoundLiveSearch => ViewModel?.LiveSearch is bool live ? live : true;
    private MeshSearchRenderMode BoundRenderMode => ViewModel?.RenderMode is MeshSearchRenderMode mode ? mode : MeshSearchRenderMode.Grouped;

    // Config objects
    private SectionConfig? BoundSections => ViewModel?.Sections;
    private GridConfig BoundGrid => ViewModel?.Grid ?? new GridConfig();

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
        }

        // Re-query when VisibleQuery changes from parent (e.g. picker typing)
        if (_initialized && BoundVisibleQuery != _lastBoundVisibleQuery)
        {
            _lastBoundVisibleQuery = BoundVisibleQuery;
            _currentValue = BoundVisibleQuery;
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
            if (monacoEditor != null && !string.IsNullOrEmpty(BoundVisibleQuery))
            {
                await monacoEditor.SetValueAsync(BoundVisibleQuery);
            }

            // If we have pre-computed groups, no need to fetch
            if (IsPrecomputedMode)
            {
                _isLoading = false;
                StateHasChanged();
                return;
            }

            // Client-side query mode
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
            return nodes.OrderBy(n => n.DisplayOrder).ThenBy(n => n.Name).ToList();

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

    /// <summary>
    /// Gets the groups to render, preferring pre-computed over locally computed.
    /// </summary>
    private IReadOnlyList<SearchResultGroup> GetGroups()
    {
        if (IsPrecomputedMode)
            return BoundPrecomputedGroups!.Groups;
        return _computedGroups?.Groups ?? [];
    }

    /// <summary>
    /// Renders grouped results using SearchResultGroup.
    /// </summary>
    private RenderFragment RenderGroupedResults() => builder =>
    {
        var groups = GetGroups();
        var seq = 0;

        var skipHeaders = groups.Count == 1;

        foreach (var group in groups)
        {
            var isCollapsed = _collapsedGroups.Contains(group.GroupKey);
            var showCollapsible = (BoundSections?.Collapsible ?? true) && !skipHeaders;
            var showCounts = BoundSections?.ShowCounts ?? true;

            var headerLabel = showCounts ? $"{group.Label} ({group.TotalCount})" : group.Label;

            builder.OpenElement(seq++, "div");
            builder.AddAttribute(seq++, "class", "mesh-search-section");

            if (!skipHeaders)
            {
                if (showCollapsible)
                {
                    builder.OpenElement(seq++, "div");
                    builder.AddAttribute(seq++, "class", "mesh-search-section-header");
                    var groupKey = group.GroupKey;
                    builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => ToggleGroup(groupKey)));

                    builder.OpenElement(seq++, "span");
                    builder.AddAttribute(seq++, "class", isCollapsed ? "mesh-search-collapse-icon" : "mesh-search-collapse-icon expanded");
                    builder.AddContent(seq++, "\u25b6");
                    builder.CloseElement();

                    builder.OpenElement(seq++, "span");
                    builder.AddAttribute(seq++, "class", "mesh-search-section-label");
                    builder.AddContent(seq++, headerLabel);
                    builder.CloseElement();

                    builder.CloseElement();
                }
                else
                {
                    builder.OpenElement(seq++, "h3");
                    builder.AddAttribute(seq++, "class", "mesh-search-section-title");
                    builder.AddContent(seq++, headerLabel);
                    builder.CloseElement();
                }
            }

            if (!isCollapsed)
            {
                builder.OpenElement(seq++, "div");
                builder.AddAttribute(seq++, "class", "mesh-search-grid-wrapper");

                if (!string.IsNullOrEmpty(BoundItemArea))
                {
                    // Use plain CSS grid for LayoutAreaView items (FluentGrid sizing doesn't work with nested LayoutAreaView)
                    builder.OpenElement(seq++, "div");
                    builder.AddAttribute(seq++, "class", "mesh-search-area-grid");

                    foreach (var item in group.Items)
                    {
                        if (item is not MeshNode node) continue;
                        RenderNodeCard(builder, node);
                    }

                    builder.CloseElement();
                }
                else
                {
                    builder.OpenComponent<FluentGrid>(seq++);
                    builder.AddAttribute(seq++, "Spacing", BoundGrid.Spacing);
                    builder.AddAttribute(seq++, "Justify", Microsoft.FluentUI.AspNetCore.Components.JustifyContent.FlexStart);
                    builder.AddAttribute(seq++, "Style", "width: 100%;");
                    builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(gridBuilder =>
                    {
                        var gridSeq = 0;

                        foreach (var item in group.Items)
                        {
                            if (item is not MeshNode node) continue;

                            gridBuilder.OpenComponent<FluentGridItem>(gridSeq++);
                            gridBuilder.AddAttribute(gridSeq++, "xs", BoundGrid.Xs);
                            gridBuilder.AddAttribute(gridSeq++, "sm", BoundGrid.Sm);
                            gridBuilder.AddAttribute(gridSeq++, "md", BoundGrid.Md);
                            gridBuilder.AddAttribute(gridSeq++, "lg", BoundGrid.Lg);
                            gridBuilder.AddAttribute(gridSeq++, "Style", "width: 100%;");
                            gridBuilder.AddAttribute(gridSeq++, "ChildContent", (RenderFragment)(itemBuilder =>
                            {
                                RenderNodeCard(itemBuilder, node);
                            }));
                            gridBuilder.CloseComponent();
                        }
                    }));
                    builder.CloseComponent();
                }
                builder.CloseElement();

                if (group.Items.Count < group.TotalCount)
                {
                    builder.OpenElement(seq++, "div");
                    builder.AddAttribute(seq++, "class", "mesh-search-show-more");
                    builder.OpenElement(seq++, "span");
                    builder.AddAttribute(seq++, "style", "color: var(--neutral-foreground-hint);");
                    builder.AddContent(seq++, $"Showing {group.Items.Count} of {group.TotalCount}");
                    builder.CloseElement();
                    builder.CloseElement();
                }
            }

            builder.CloseElement();
        }
    };

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

    private void RenderNodeCard(RenderTreeBuilder builder, MeshNode node)
    {
        if (!string.IsNullOrEmpty(BoundItemArea))
        {
            RenderLayoutAreaItem(builder, node);
            return;
        }
        var seq = 0;
        RenderThumbnailCard(builder, node, ref seq);
    }

    private void RenderLayoutAreaItem(RenderTreeBuilder builder, MeshNode node)
    {
        var seq = 0;
        var layoutArea = new LayoutAreaControl(
            node.Path,
            new LayoutAreaReference(BoundItemArea!))
            .WithShowProgress(false);

        builder.OpenComponent<LayoutAreaView>(seq++);
        builder.AddAttribute(seq++, "ViewModel", layoutArea);
        builder.AddAttribute(seq++, "Stream", Stream);
        builder.AddAttribute(seq++, "Area", $"search-{node.Path}-{BoundItemArea}");
        builder.CloseComponent();
    }

    private void RenderThumbnailCard(RenderTreeBuilder builder, MeshNode node, ref int seq)
    {
        var imageUrl = GetImageUrl(node);
        var title = node.Name ?? node.Id;
        var description = node.NodeType ?? "";
        var initial = !string.IsNullOrEmpty(title) ? title[0].ToString().ToUpper() : "?";
        var isSelected = !string.IsNullOrEmpty(SelectedPath) && node.Path == SelectedPath;
        var isPickerMode = OnNodeSelected.HasDelegate;

        var cardClass = isSelected ? "mesh-search-card mesh-search-card-selected" : "mesh-search-card";

        builder.OpenComponent<FluentCard>(seq++);
        builder.AddAttribute(seq++, "Class", cardClass);
        builder.AddAttribute(seq++, "Style", "cursor: pointer;");
        builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(cardBuilder =>
        {
            var cardSeq = 0;

            if (isPickerMode)
            {
                // Picker mode: clicking selects the node
                cardBuilder.OpenElement(cardSeq++, "div");
                var capturedNode = node;
                cardBuilder.AddAttribute(cardSeq++, "onclick", EventCallback.Factory.Create(this, () => OnNodeSelected.InvokeAsync(capturedNode)));
                cardBuilder.AddAttribute(cardSeq++, "style", "display: flex; flex-direction: row; align-items: center; gap: 12px; padding: 8px; min-height: 60px; height: 76px; cursor: pointer; color: inherit;");
            }
            else
            {
                // Navigation mode: clicking navigates
                cardBuilder.OpenElement(cardSeq++, "a");
                cardBuilder.AddAttribute(cardSeq++, "href", $"/{node.Path}");
                cardBuilder.AddAttribute(cardSeq++, "style", "display: flex; flex-direction: row; align-items: center; gap: 12px; padding: 8px; min-height: 60px; height: 76px; text-decoration: none; color: inherit;");
            }

            if (!string.IsNullOrEmpty(imageUrl))
            {
                cardBuilder.OpenElement(cardSeq++, "img");
                cardBuilder.AddAttribute(cardSeq++, "src", imageUrl);
                cardBuilder.AddAttribute(cardSeq++, "alt", title);
                cardBuilder.AddAttribute(cardSeq++, "style", "width: 48px; height: 48px; min-width: 48px; min-height: 48px; max-width: 48px; max-height: 48px; border-radius: 8px; object-fit: cover; flex-shrink: 0;");
                cardBuilder.CloseElement();
            }
            else
            {
                cardBuilder.OpenElement(cardSeq++, "div");
                cardBuilder.AddAttribute(cardSeq++, "style", "width: 48px; height: 48px; min-width: 48px; min-height: 48px; border-radius: 8px; background: var(--accent-fill-rest, #0078d4); color: var(--foreground-on-accent-rest, white); display: flex; align-items: center; justify-content: center; font-weight: bold; font-size: 18px; flex-shrink: 0;");
                cardBuilder.AddContent(cardSeq++, initial);
                cardBuilder.CloseElement();
            }

            cardBuilder.OpenElement(cardSeq++, "div");
            cardBuilder.AddAttribute(cardSeq++, "style", "flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 4px;");

            cardBuilder.OpenElement(cardSeq++, "div");
            cardBuilder.AddAttribute(cardSeq++, "style", "font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;");
            cardBuilder.AddContent(cardSeq++, title);
            cardBuilder.CloseElement();

            if (!string.IsNullOrEmpty(description))
            {
                cardBuilder.OpenElement(cardSeq++, "div");
                cardBuilder.AddAttribute(cardSeq++, "style", "font-size: 12px; color: var(--neutral-foreground-hint, #666); overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; line-height: 1.4;");
                cardBuilder.AddContent(cardSeq++, Truncate(description, 100));
                cardBuilder.CloseElement();
            }

            cardBuilder.CloseElement();
            cardBuilder.CloseElement(); // close a or div
        }));
        builder.CloseComponent();
    }

    private void NavigateTo(string path)
    {
        NavigationManager.NavigateTo($"/{path}");
    }

    private string? GetImageUrl(MeshNode node)
    {
        if (node.Content is JsonElement json)
        {
            if (json.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.String)
                return avatar.GetString();
            if (json.TryGetProperty("Avatar", out var Avatar) && Avatar.ValueKind == JsonValueKind.String)
                return Avatar.GetString();
            if (json.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.String)
                return logo.GetString();
            if (json.TryGetProperty("Logo", out var Logo) && Logo.ValueKind == JsonValueKind.String)
                return Logo.GetString();
        }
        // Fall back to node.Icon only if it looks like an image URL (not a Fluent icon name)
        return MeshNodeImageHelper.GetIconAsImageUrl(node.Icon);
    }

    private string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text.Substring(0, maxLength - 3) + "..." : text;
    }

    public void Dispose()
    {
        _reactiveSubscription?.Dispose();
    }
}
