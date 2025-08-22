using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;

namespace MeshWeaver.ContentCollections;

public static class ArticleCatalogLayoutArea
{
    /// <summary>
    /// Catalog area for articles.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="ctx">Rendering context</param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public static async Task<UiControl?> Catalog(LayoutAreaHost host, RenderingContext _, CancellationToken ct)
    {
        var articleService = host.Hub.GetContentService();
        var articles = await articleService.GetArticleCatalog(ParseToOptions(host.Reference), ct);
        return articles.Aggregate(Controls.Stack.AddSkin(new ArticleCatalogSkin()), (s, a) =>
                        s.WithView(CreateControl(a))
                    )
                    .WithPageTitle("Articles")
            ;
    }

    private static ArticleCatalogItemControl CreateControl(Article a) =>
        new(a with { Content = string.Empty, PrerenderedHtml = string.Empty });

    /// <summary>
    /// This is the deserialization of Id to catalog options. Need to see how we use.
    /// </summary>
    /// <param name="reference">Layout area reference to be parsed.</param>
    /// <returns></returns>
    private static ArticleCatalogOptions ParseToOptions(LayoutAreaReference reference)
    {
        // TODO V10: Need to create some link from layout area reference id to options ==> url parsing. (24.01.2025, Roland Bürgi)
        return new()
        {
            Collection = reference.Id?.ToString() ?? string.Empty
        };
    }

    public static NavMenuControl ArticlesNavMenu(this NavMenuControl menu, string collection, string? displayName = null)
        => menu.WithView(Controls.NavLink(displayName ?? collection.Wordify(), $"articles/{collection}"));



}
