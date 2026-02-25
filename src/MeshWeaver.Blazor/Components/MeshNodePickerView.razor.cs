using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class MeshNodePickerView : FormComponentBase<MeshNodePickerControl, MeshNodePickerView, string>
{
    [Inject]
    private IMeshQuery MeshQuery { get; set; } = default!;

    private MeshNode? _selectedNode;
    private bool _isSearchOpen;
    private bool _isLoading;
    private string _searchText = "";
    private ElementReference _textFieldElement;
    private List<MeshNode> _results = new();
    private List<MeshNode>? _cachedResults;

    private MeshNode[]? BoundItems { get; set; }
    private bool HasItems => BoundItems is { Length: > 0 };

    protected override void BindData()
    {
        DataBind(ViewModel.Items, x => x.BoundItems, ConvertItems);
        base.BindData();
    }

    private MeshNode[]? ConvertItems(object? value, MeshNode[]? defaultValue)
    {
        if (value is object[] arr && arr.Length > 0)
        {
            return arr.Select(item => item switch
            {
                MeshNode node => node,
                JsonElement je => je.Deserialize<MeshNode>(Hub.JsonSerializerOptions),
                _ => null
            }).Where(n => n != null).ToArray()!;
        }
        if (value is JsonElement je2 && je2.ValueKind == JsonValueKind.Array)
        {
            return je2.Deserialize<MeshNode[]>(Hub.JsonSerializerOptions);
        }
        return defaultValue;
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (!string.IsNullOrEmpty(Value) && (_selectedNode == null || _selectedNode.Path != Value))
        {
            _ = ResolveSelectedNodeAsync();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (_isSearchOpen && _textFieldElement.Id != null)
        {
            try
            {
                await _textFieldElement.FocusAsync();
            }
            catch
            {
                // Element may not be in DOM yet
            }
        }
    }

    private string[] GetQueries()
    {
        return ViewModel?.Queries ?? [];
    }

    private void OpenSearch()
    {
        _isSearchOpen = true;
        _ = LoadResultsAsync();
        StateHasChanged();
    }

    private void CloseSearch()
    {
        _isSearchOpen = false;
        StateHasChanged();
    }

    private void OnSearchInput(ChangeEventArgs e)
    {
        _searchText = e.Value?.ToString() ?? "";
        _ = LoadResultsAsync();
        if (!_isSearchOpen)
            _isSearchOpen = true;
        StateHasChanged();
    }

    private async Task LoadResultsAsync()
    {
        // When Items are provided and already cached, filter in-memory
        if (HasItems && _cachedResults != null)
        {
            _results = FilterCached(_searchText.Trim());
            await InvokeAsync(StateHasChanged);
            return;
        }

        _isLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var queryResults = new List<MeshNode>();

            // Execute queries (if any)
            var queries = GetQueries();
            if (queries.Length > 0)
            {
                var userText = _searchText.Trim();
                var tasks = queries.Select(async baseQuery =>
                {
                    // When Items are set, don't append user text — we filter in-memory
                    var fullQuery = HasItems || string.IsNullOrEmpty(userText)
                        ? baseQuery
                        : $"{baseQuery} {userText}";
                    try
                    {
                        return await MeshQuery.QueryAsync<MeshNode>(fullQuery).ToListAsync();
                    }
                    catch
                    {
                        return new List<MeshNode>();
                    }
                });

                var allResults = await Task.WhenAll(tasks);
                queryResults = allResults.SelectMany(batch => batch).ToList();
            }

            // Merge Items + query results, deduplicate by Path (Items take precedence)
            var items = BoundItems ?? [];
            var merged = items.AsEnumerable()
                .Concat(queryResults)
                .GroupBy(n => n.Path)
                .Select(g => g.First())
                .ToList();

            if (HasItems)
            {
                // Cache for in-memory filtering
                _cachedResults = merged;
                _results = FilterCached(_searchText.Trim());
            }
            else
            {
                _results = merged;
            }
        }
        catch
        {
            _results = new List<MeshNode>();
        }
        finally
        {
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private List<MeshNode> FilterCached(string searchText)
    {
        if (_cachedResults == null) return new List<MeshNode>();
        if (string.IsNullOrEmpty(searchText)) return _cachedResults;

        return _cachedResults
            .Where(n =>
                (n.Name ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.Path ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.NodeType ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || (n.Id ?? "").Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void SelectNode(MeshNode node)
    {
        _selectedNode = node;
        Value = node.Path;
        _isSearchOpen = false;
        _searchText = "";
        // Directly update the data pointer — the debounced pipeline in
        // FormComponentBase uses Skip(1) which swallows single-selection updates.
        if (ViewModel?.Data is JsonPointerReference pointer)
            UpdatePointer(node.Path, pointer);
        StateHasChanged();
    }

    private void ClearSelection()
    {
        _selectedNode = null;
        Value = "";
        _searchText = "";
        if (ViewModel?.Data is JsonPointerReference pointer)
            UpdatePointer("", pointer);
        StateHasChanged();
    }

    private async Task ResolveSelectedNodeAsync()
    {
        if (string.IsNullOrEmpty(Value)) return;

        // First check Items for the selected node (avoids query round-trip)
        var items = BoundItems ?? [];
        if (items.Length > 0)
        {
            _selectedNode = items.FirstOrDefault(n =>
                string.Equals(n.Path, Value, StringComparison.OrdinalIgnoreCase));
            if (_selectedNode != null)
            {
                StateHasChanged();
                return;
            }
        }

        try
        {
            var nodes = await MeshQuery
                .QueryAsync<MeshNode>($"path:{Value}")
                .ToListAsync();
            _selectedNode = nodes.FirstOrDefault();
            StateHasChanged();
        }
        catch
        {
            _selectedNode = new MeshNode(Value) { Name = Value };
        }
    }
}
