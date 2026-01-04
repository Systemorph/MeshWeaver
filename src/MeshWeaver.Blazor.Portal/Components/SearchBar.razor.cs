using System.Diagnostics;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Portal.Components;

public partial class SearchBar : IAsyncDisposable
{
    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    [Inject]
    public IMeshQuery? MeshQuery { get; set; }

    private FluentAutocomplete<MeshNode>? searchAutocomplete;
    private string? searchTerm;
    private IEnumerable<MeshNode> selectedOptions = [];

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

    private async Task HandleSearchInputAsync(OptionsSearchEventArgs<MeshNode> e)
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

        var results = await MeshQuery.QueryAsync<MeshNode>(request).ToArrayAsync();

        e.Items = results;
    }


    private void HandleSearchClicked()
    {
        searchTerm = null;
        var targetHref = selectedOptions.SingleOrDefault()?.Path;
        selectedOptions = [];
        InvokeAsync(StateHasChanged);

        // Ignore clearing the search bar
        if (targetHref is null)
        {
            return;
        }

        NavigationManager.NavigateTo(targetHref ?? throw new UnreachableException("Item has no href"));
    }

    private void HandleKeyDown(KeyboardEventArgs e)
    {
        // Navigate to search page when Enter is pressed (and no autocomplete item is selected)
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(searchTerm) && !selectedOptions.Any())
        {
            var encodedQuery = Uri.EscapeDataString(searchTerm);
            NavigationManager.NavigateTo($"/search?q={encodedQuery}");
        }
    }

    public ValueTask DisposeAsync()
    {
        KeyCodeService.UnregisterListener(OnKeyDownAsync, OnKeyDownAsync);
        return ValueTask.CompletedTask;
    }

}
