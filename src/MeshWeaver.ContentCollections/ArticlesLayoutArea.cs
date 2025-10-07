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
    /// Uses configured addresses if specified, otherwise shows articles from the current hub address only.
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="_">The rendering context</param>
    /// <returns>An Article Catalog control</returns>
    public static UiControl? Articles(LayoutAreaHost host, RenderingContext _)
    {
        var configuration = host.Hub.ServiceProvider.GetRequiredService<ArticlesConfiguration>();
        var selectedCollection = host.Reference.Id?.ToString();

        return new ArticleCatalogControl
        {
            Collections = selectedCollection,
            Addresses = configuration.Addresses
        };
    }

    /// <summary>
    /// Content layout area for displaying a single article
    /// </summary>
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var split = host.Reference.Id?.ToString()?.Split("/");
        if (split is null || split.Length < 2)
            return Observable.Return(new MarkdownControl("Path must be specified"));

        var collection = split[0];
        var path = string.Join('/', split.Skip(1));

        var articleService = host.Hub.GetContentService();
        var contentStream = articleService.GetArticle(collection, path);

        if (contentStream is null)
            return Observable.Return(new MarkdownControl($"Article not found: {path}"));

        return contentStream.Select(article =>
            article is Article a ? (UiControl?)new ArticleControl(a) : new MarkdownControl($"Article not found: {path}")
        );
    }
}
