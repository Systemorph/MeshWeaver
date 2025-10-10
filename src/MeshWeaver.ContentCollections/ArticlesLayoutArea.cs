using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Layout area for displaying article catalog
/// </summary>
public static class ArticlesLayoutArea
{
    /// <summary>
    /// Articles layout area that displays the article catalog for a specific collection.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="_">The rendering context</param>
    /// <returns>An Article Catalog control</returns>
    [Browsable(false)]
    public static UiControl? Articles(LayoutAreaHost host, RenderingContext _)
    {
        var selectedCollection = host.Reference.Id?.ToString();
        if (selectedCollection is not null)
            return GetCollectionFromId(host, selectedCollection);

        var config = host.Hub.ServiceProvider.GetRequiredService<ArticlesConfiguration>();
        return new ArticleCatalogControl
        {
            Collections = config.Collections,
            CollectionConfigurations = config.CollectionConfigurations
        };
    }

    private static UiControl? GetCollectionFromId(LayoutAreaHost host, string selectedCollection)
    {
        var contentService = host.Hub.GetContentService();
        var collectionConfig = contentService.GetCollectionConfig(selectedCollection);
        if (collectionConfig == null)
            return new MarkdownControl($"Collection not found: {selectedCollection}");
        return new ArticleCatalogControl
        {
            Collections = new[] { selectedCollection },
            CollectionConfigurations = new[] { collectionConfig }
        };
    }

    /// <summary>
    /// Content layout area for displaying a single article
    /// </summary>
    [Browsable(false)]
    public static async Task<IObservable<UiControl?>> Content(LayoutAreaHost host, RenderingContext _, CancellationToken ct)
    {
        var split = host.Reference.Id?.ToString()?.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified"));

        var collection = split[0];
        var path = string.Join('/', split.Skip(1));

        var articleService = host.Hub.GetContentService();
        var contentStream = await articleService.GetArticleAsync(collection, path, ct);


        return contentStream.Select(article =>
            article is Article a ? (UiControl?)new ArticleControl(a) : new MarkdownControl($"Article not found: {path}")
        );
    }
}
