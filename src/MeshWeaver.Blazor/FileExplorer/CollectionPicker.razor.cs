using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class CollectionPicker
{
    private IReadOnlyCollection<Option<string>>? collections;
    [Inject] private IContentService ContentService { get; set; } = null!;
    [Parameter] public string? NullLabel { get; set; }
    private string? SelectedCollection { get; set; }
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        SelectedCollection = Collection;
        var definedCollections = await ContentService.GetCollectionsAsync();

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
        if(collection == NullLabel)
            collection = null;
        SelectedCollection = collection;
        await CollectionChanged.InvokeAsync(collection);
        await InvokeAsync(StateHasChanged);
    }

}
