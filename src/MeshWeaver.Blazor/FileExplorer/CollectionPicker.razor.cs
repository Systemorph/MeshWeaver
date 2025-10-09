using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.FileExplorer;

public partial class CollectionPicker : ComponentBase
{
    private IReadOnlyCollection<Option<string>>? collections;
    [Inject] private IMessageHub Hub { get; set; } = null!;

    private IContentService ContentService => Hub.ServiceProvider.GetRequiredService<IContentService>();
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Parameter] public string? NullLabel { get; set; }
    [Parameter] public string? Collection { get; set; }
    [Parameter] public EventCallback<string?> CollectionChanged { get; set; }
    [Parameter] public string? Context { get; set; }
    [Parameter] public bool UseNavigation { get; set; } = false;
    private string? SelectedCollection { get; set; }
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        SelectedCollection = Collection;

        var definedCollections = !string.IsNullOrEmpty(Context)
            ? ContentService.GetCollections(Context)
            : await ContentService.GetCollectionsAsync();

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

        if (UseNavigation && !string.IsNullOrEmpty(collection))
        {
            NavigationManager.NavigateTo($"/collections/{collection}");
        }
        else
        {
            await CollectionChanged.InvokeAsync(collection);
            await InvokeAsync(StateHasChanged);
        }
    }

}
