using MeshWeaver.ContentCollections;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Blazor.Articles;

public partial class ArticleCatalogView
{
    private string? selectedCollection;
    private IReadOnlyCollection<Article> articles = [];
    private IReadOnlyCollection<Option<string>>? collectionOptions;
    private readonly IReadOnlyCollection<ContentCollectionConfig>? collectionConfigurations;
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

        // Bind to CollectionConfigurations property
        DataBind(
            ViewModel.CollectionConfigurations,
            x => x.collectionConfigurations
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
        var allCollections = await LoadCollectionsAsync(default)
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
    private async IAsyncEnumerable<ContentCollection> LoadCollectionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var contentService = Hub.ServiceProvider.GetRequiredService<IContentService>();
        var allCollections = new HashSet<string>(collections ?? []);
        foreach (var config in collectionConfigurations ?? [])
        {
            contentService.AddConfiguration(config);
            allCollections.Add(config.Name);
        }

        foreach (var allCollection in allCollections)
        {
            var col = await contentService.GetCollectionAsync(allCollection, ct);
            if (col != null)
                yield return col;
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
        // Format: Content/{collection}/{path}
        var collection = article.Collection;
        var path = article.Path;

        return $"Content/{collection}/{path}";
    }
}
