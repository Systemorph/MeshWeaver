using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Views;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;

public static class ArticleCatalogLayoutArea
{
    public static IObservable<object> Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var collectionName = host.Hub.Address.GetCollectionName();
        var collection = host.Hub.GetCollection(collectionName);
        return
            collection.GetArticles(ParseToOptions(host.Reference))
                .Select(x =>
                    x.Aggregate(Controls.Stack.AddSkin(new ArticleCatalogSkin()), (s, a) =>
                        s.WithView(CreateControl(a))
                    )
                )


            ;
    }

    private static ArticleCatalogItemControl CreateControl(Article a) =>
        new(a with { Content = null, PrerenderedHtml = null });

    private static ArticleCatalogOptions ParseToOptions(LayoutAreaReference reference)
    {
        // TODO V10: Need to create some link from layout area reference id to options ==> url parsing. (24.01.2025, Roland Bürgi)
        return new();
    }

    public static NavMenuControl ArticlesNavMenu(this NavMenuControl menu, string collection, string displayName = null)
        => menu.WithView(Controls.NavLink(displayName ?? collection.Wordify(), $"articles/{collection}"));


    public static LayoutAreaReference GetCatalogLayoutReference(string path, Func<ArticleCatalogOptions, ArticleCatalogOptions> options = null)
        => new(nameof(Catalog)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)

}
