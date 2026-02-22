using System.Reactive.Linq;
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
        _isLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var queries = GetQueries();
            if (queries.Length == 0)
            {
                _results = new List<MeshNode>();
                return;
            }

            var userText = _searchText.Trim();

            var tasks = queries.Select(async baseQuery =>
            {
                var fullQuery = string.IsNullOrEmpty(userText)
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

            _results = allResults
                .SelectMany(batch => batch)
                .GroupBy(n => n.Path)
                .Select(g => g.First())
                .ToList();
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
