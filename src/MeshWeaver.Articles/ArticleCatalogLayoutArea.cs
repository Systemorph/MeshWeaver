﻿using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;

public static class ArticleCatalogLayoutArea
{
    /// <summary>
    /// Catalog area for articles.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="ctx">Rendering context</param>
    /// <returns></returns>
    public static IObservable<object> Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var collection = host.Hub.GetArticleService();
        return
            collection.GetArticleCatalog(ParseToOptions(host.Reference))
                .Select(x =>
                    x.Aggregate(Controls.Stack.AddSkin(new ArticleCatalogSkin()), (s, a) =>
                        s.WithView(CreateControl(a))
                    )
                    .WithPageTitle("Articles")
                )
            ;
    }

    private static ArticleCatalogItemControl CreateControl(Article a) =>
        new(a with { Content = null, PrerenderedHtml = null });

    /// <summary>
    /// This is the deserialization of Id to catalog options. Need to see how we use.
    /// </summary>
    /// <param name="reference">Layout area reference to be parsed.</param>
    /// <returns></returns>
    private static ArticleCatalogOptions ParseToOptions(LayoutAreaReference reference)
    {
        // TODO V10: Need to create some link from layout area reference id to options ==> url parsing. (24.01.2025, Roland Bürgi)
        return new();
    }

    public static NavMenuControl ArticlesNavMenu(this NavMenuControl menu, string collection, string displayName = null)
        => menu.WithView(Controls.NavLink(displayName ?? collection.Wordify(), $"articles/{collection}"));


    /// <summary>
    /// Gets a LayoutAreaReference for the article catalog with the query options.
    /// </summary>
    /// <param name="path"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static LayoutAreaReference GetCatalogLayoutReference(string path, Func<ArticleCatalogOptions, ArticleCatalogOptions> options = null)
        => new(nameof(Catalog)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)

}
