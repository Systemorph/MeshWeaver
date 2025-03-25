using MeshWeaver.Articles;
using MeshWeaver.Blazor.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Pages;

public partial class ArticlesCatalogPage
{
    private string collectionFilter;
    private IReadOnlyCollection<Option<string>> collections;
    [Inject] private IArticleService ArticleService { get; set; }


    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        collectionFilter = Collection;
        var definedCollections = await ArticleService.GetCollectionsAsync();
        collections = definedCollections
            .Select(a => new Option<string>()
            {
                Text = a.DisplayName,
                Value = a.Collection
            }).Prepend(new Option<string>(){Text = "all"})
            .ToArray();
    }

    private void SelectionChanged(string filter)
    {
        collectionFilter = filter == "all" ? null : filter;
        InvokeAsync(StateHasChanged);
    }

}
