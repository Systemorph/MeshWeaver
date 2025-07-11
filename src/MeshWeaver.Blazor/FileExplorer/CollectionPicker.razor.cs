using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class CollectionPicker : ComponentBase
{
    private IReadOnlyCollection<Option<string>>? collections;
    [Inject] private IContentService ContentService { get; set; } = null!;
    [Parameter] public string? NullLabel { get; set; }
    [Parameter] public string? Collection { get; set; }
    [Parameter] public EventCallback<string?> CollectionChanged { get; set; }
    [Parameter] public bool ShowHidden { get; set; } = false;
    [Parameter] public string? Context { get; set; }
    private string? SelectedCollection { get; set; }
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        SelectedCollection = Collection;

        var definedCollections = !string.IsNullOrEmpty(Context)
            ? ContentService.GetCollections(Context)
            : ContentService.GetCollections(ShowHidden);

        var options = definedCollections
            .Select(a => new Option<string>() { Text = a.DisplayName, Value = a.Collection });

        if (NullLabel is not null)
            options = options
                .Prepend(new Option<string>() { Text = NullLabel });

        collections = options.ToArray();
        if (NullLabel is null && SelectedCollection is null && collections!.Any())
        {
            await OnValueChanged(collections!.First().Value!);
        }
    }
    private async Task OnValueChanged(string? collection)
    {
        if (SelectedCollection == collection)
            return;
        if (collection == NullLabel)
            collection = null;
        SelectedCollection = collection;
        await CollectionChanged.InvokeAsync(collection);
        await InvokeAsync(StateHasChanged);
    }

}
