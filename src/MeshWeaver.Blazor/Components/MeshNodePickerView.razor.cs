using System.Reactive.Linq;
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

    private MeshSearchControl? _searchControl;
    private MeshNode? _selectedNode;
    private bool _isSearchOpen;

    protected override void BindData()
    {
        base.BindData();
        BuildSearchControl();
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        // If Value is set externally and we don't have a selected node, resolve it
        if (!string.IsNullOrEmpty(Value) && (_selectedNode == null || _selectedNode.Path != Value))
        {
            _ = ResolveSelectedNodeAsync();
        }
    }

    private void BuildSearchControl()
    {
        var hiddenQuery = ViewModel?.HiddenQuery?.ToString() ?? "";
        var ns = ViewModel?.Namespace?.ToString() ?? "";

        _searchControl = new MeshSearchControl()
        {
            HiddenQuery = hiddenQuery,
            Namespace = ns,
            ShowSearchBox = true,
            LiveSearch = true,
            Placeholder = Placeholder ?? "Search...",
        };

        if (ViewModel?.MaxResults != null)
        {
            _searchControl = _searchControl with
            {
                Sections = new SectionConfig { ItemLimit = int.TryParse(ViewModel.MaxResults.ToString(), out var max) ? max : 10 }
            };
        }
    }

    private void OpenSearch()
    {
        _isSearchOpen = true;
        StateHasChanged();
    }

    private void CloseSearch()
    {
        _isSearchOpen = false;
        StateHasChanged();
    }

    private async Task SelectNode(MeshNode node)
    {
        _selectedNode = node;
        Value = node.Path;
        _isSearchOpen = false;
        await Task.CompletedTask;
        StateHasChanged();
    }

    private void ClearSelection()
    {
        _selectedNode = null;
        Value = "";
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
            // If resolution fails, show path as fallback
            _selectedNode = new MeshNode(Value) { Name = Value };
        }
    }
}
