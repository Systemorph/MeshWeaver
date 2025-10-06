using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleCatalogView
{
    private string? selectedCollection;
    private IReadOnlyCollection<Article> articles = [];
    private IReadOnlyCollection<Option<string>>? collectionOptions;
    private readonly IReadOnlyCollection<string> collections;
    private readonly IReadOnlyCollection<Address> addresses;
    protected override void BindData()
    {
        // Bind to Collection property
        DataBind(
            ViewModel.Collections,
            x => x.collections
        );

        // Bind to Addresses property
        DataBind(
            ViewModel.Addresses,
            x => x.addresses
        );


    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();
        var allCollections = await LoadCollectionsAsync()
            .ToArrayAsync();
        var options = allCollections
            .Select(c => new Option<string>
            {
                Text = c.Collection,
                Value = c.Collection
            })
            .ToList();

        // Add "all" option if showing picker
        if (options.Count > 1)
        {
            options.Insert(0, new Option<string> { Text = "(all)", Value = null });
        }

        collectionOptions = options;
        articles = await LoadArticlesAsync();
    }

    private async IAsyncEnumerable<ContentCollection> LoadCollectionsAsync()
    {
        var contentService = Hub.ServiceProvider.GetRequiredService<IContentService>();

        // Get collectionOptions from the specified addresses
        if (addresses != null && addresses.Any())
        {
            foreach (var address in addresses)
            {
                var collection = await contentService.GetCollectionForAddressAsync(address);
                if (collection != null)
                {
                    yield return collection;
                }
            }
        }

    }

    private Task<IReadOnlyCollection<Article>> LoadArticlesAsync()
    {
        var contentService = Hub.ServiceProvider.GetRequiredService<IContentService>();
        var catalogOptions = !string.IsNullOrEmpty(selectedCollection)
        ? GetCatalogOptionsFromSelection(selectedCollection)
        : new ArticleCatalogOptions
        {
            Collections = collections,
            Addresses = addresses,
        };

        return contentService.GetArticleCatalog(catalogOptions);
    }

    private ArticleCatalogOptions GetCatalogOptionsFromSelection(string s)
    {
        var fromAddress = addresses.FirstOrDefault(a => a.ToString() == s);
        if (fromAddress != null)
            return new ArticleCatalogOptions() { Addresses = [fromAddress] };
        return new ArticleCatalogOptions
        {
            Collections = [s],
        };
    }

    private string? SelectedCollection
    {
        get => selectedCollection;
        set
        {
            if (selectedCollection != value)
            {
                selectedCollection = value;
                InvokeAsync(StateHasChanged);
            }
        }
    }

    private IReadOnlyCollection<Article> Articles => articles;
    private bool ShowPicker => collectionOptions?.Count > 0;
}
