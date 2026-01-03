using System.Diagnostics;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public IMeshQuery? MeshQuery { get; set; }

    private FluentAutocomplete<NavItem>? searchAutocomplete;
    private string? searchTerm;
    private IEnumerable<NavItem> selectedOptions = [];

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs? args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            searchAutocomplete?.Element?.FocusAsync();
        }
        return Task.CompletedTask;
    }

    private async Task HandleSearchInputAsync(OptionsSearchEventArgs<NavItem> e)
    {
        searchTerm = e.Text;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            e.Items = null;
            return;
        }

        if (MeshQuery == null)
        {
            e.Items = null;
            return;
        }

        // Search for MeshNodes matching the query using GitHub syntax
        var request = new MeshQueryRequest
        {
            Query = searchTerm,
            Limit = 10
        };

        var results = new List<NavItem>();
        await foreach (var item in MeshQuery.QueryAsync(request))
        {
            if (item is MeshNode node)
            {
                var path = !string.IsNullOrEmpty(node.Namespace)
                    ? $"{node.Namespace}/{node.Id}"
                    : node.Id;

                var icon = GetIconForNodeType(node.NodeType);
                results.Add(new NavItem(node.Name ?? node.Id, $"/{path}", icon));
            }

            if (results.Count >= 10) break;
        }

        e.Items = results;
    }

    private static Icon GetIconForNodeType(string? nodeType)
    {
        return nodeType switch
        {
            "Agent" => new Icons.Regular.Size16.Bot(),
            "Story" => new Icons.Regular.Size16.Notebook(),
            "Project" => new Icons.Regular.Size16.Folder(),
            "Organization" => new Icons.Regular.Size16.Building(),
            "Person" => new Icons.Regular.Size16.Person(),
            "Document" => new Icons.Regular.Size16.Document(),
            _ => new Icons.Regular.Size16.Circle()
        };
    }

    private void HandleSearchClicked()
    {
        searchTerm = null;
        var targetHref = selectedOptions.SingleOrDefault()?.Href;
        selectedOptions = [];
        InvokeAsync(StateHasChanged);

        // Ignore clearing the search bar
        if (targetHref is null)
        {
            return;
        }

        NavigationManager.NavigateTo(targetHref ?? throw new UnreachableException("Item has no href"));
    }

    public ValueTask DisposeAsync()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        return ValueTask.CompletedTask;
    }

}
