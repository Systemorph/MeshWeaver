using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Portal.Shared.Web.Components;

public partial class SearchBar : IAsyncDisposable
{
    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required IKeyCodeService KeyCodeService { get; set; }

    private FluentAutocomplete<NavItem> searchAutocomplete = default!;
    private string searchTerm = "";
    private IEnumerable<NavItem> selectedOptions = [];

    protected override void OnInitialized()
    {
        KeyCodeService.RegisterListener(OnKeyDownAsync);
    }

    public Task OnKeyDownAsync(FluentKeyCodeEventArgs args)
    {
        if (args is not null && args.Key == KeyCode.Slash)
        {
            searchAutocomplete?.Element?.FocusAsync();
        }
        //StateHasChanged();
        return Task.CompletedTask;
    }

    private void HandleSearchInput(OptionsSearchEventArgs<NavItem> e)
    {
        var searchTerm = e.Text;

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            e.Items = null;
        }
        //else
        //{
        //    e.Items = NavProvider.FlattenedMenuItems
        //        .Where(x => x.Href != null) // Ignore Group headers
        //        .Where(x => x.Title.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
        //}
    }

    private void HandleSearchClicked()
    {
        searchTerm = null;
        var targetHref = selectedOptions?.SingleOrDefault()?.Href;
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
