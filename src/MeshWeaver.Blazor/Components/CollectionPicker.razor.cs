using MeshWeaver.Articles;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Components;

public partial class CollectionPicker
{
    private IReadOnlyCollection<Option<string>> collections;
    [Inject] private IArticleService ArticleService { get; set; }
    [Parameter] public string NullLabel { get; set; } 
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        var definedCollections = await ArticleService.GetCollectionsAsync();

        var options = definedCollections
            .Select(a => new Option<string>() { Text = a.DisplayName, Value = a.Collection });

        if (NullLabel is not null)
            options = options
                .Prepend(new Option<string>() { Text = NullLabel });

        collections = options.ToArray();
        if (NullLabel is null && Collection is null && collections.Any())
        {
            await OnValueChanged(collections.First().Value);
        }
    }
    private Task OnValueChanged(string collection)
    {
        if (Collection == collection)
            return Task.CompletedTask;
        if(collection == NullLabel)
            collection = null;

        Collection = collection;
        return CollectionChanged.InvokeAsync(collection);
    }

}
