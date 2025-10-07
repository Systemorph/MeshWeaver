using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleCatalogView
{
    private string? selectedCollection;
    private IReadOnlyCollection<Article> articles = [];
    private IReadOnlyCollection<Option<string>>? collectionOptions;
    private readonly IReadOnlyCollection<Address>? addresses;
    private readonly IReadOnlyCollection<string>? collections;
    private Dictionary<string, ContentCollection> collectionsByName = new();

    [Inject] private NavigationManager NavigationManager { get; set; } = null!;

    protected override void BindData()
    {
        // Bind to collections property
        DataBind(
            ViewModel.Collections,
            x => x.collections
        );

        // Bind to Addresses property
        DataBind(
            ViewModel.Addresses,
            x => x.addresses
        );

        // Bind to selected collection property
        DataBind(
            ViewModel.SelectedCollection,
            x => x.selectedCollection
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
        collectionsByName = allCollections.ToDictionary(x => x.Collection);
        // Add "all" option if showing picker
        if (options.Count > 1)
        {
            options.Insert(0, new Option<string> { Text = All, Value = null });
        }

        collectionsByName = allCollections.ToDictionary(c => c.Collection);
        collectionOptions = options;
        articles = await LoadArticlesAsync();
    }

    private const string All = "(all)";
    private async IAsyncEnumerable<ContentCollection> LoadCollectionsAsync()
    {
        var contentService = Hub.ServiceProvider.GetRequiredService<IContentService>();

        if (collections is not null)
            foreach (var collection in collections)
            {
                var col = contentService.GetCollection(collection);
                if (col != null)
                    yield return col;
            }
        // Get collectionOptions from the specified addresses
        if (addresses is not null)
            foreach (var address in addresses)
            {
                var coll = await contentService.GetCollectionForAddressAsync(address);
                foreach (var collection in coll)
                    yield return collection;
            }



    }

    private Task<IReadOnlyCollection<Article>> LoadArticlesAsync()
    {
        var contentService = Hub.ServiceProvider.GetRequiredService<IContentService>();
        if (string.IsNullOrWhiteSpace(selectedCollection) || selectedCollection == All)
            return contentService.GetArticleCatalogAsync(new ArticleCatalogOptions
            {
                Collections = collectionsByName.Keys
            });
        return contentService.GetArticleCatalogAsync(new ArticleCatalogOptions() { Collections = [selectedCollection] });
    }

    private string? SelectedCollection
    {
        get => selectedCollection;
        set
        {
            selectedCollection = value;
            InvokeAsync(async () =>
            {
                articles = await LoadArticlesAsync();
                StateHasChanged();
            });
        }
    }

    private IReadOnlyCollection<Article> Articles => articles;
    private bool ShowPicker => collectionOptions?.Count > 1;

    private string GenerateArticleUrl(Article article)
    {
        // Format: {address}/Content/{collection}/{path}
        var address = addresses?.FirstOrDefault()?.ToString() ?? "Portal";
        var collection = article.Collection;
        var path = article.Path;

        return $"{address}/Content/{collection}/{path}";
    }
}
