using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Articles;

public static class ArticleLayouts
{
    internal static MessageHubConfiguration AddArticleViews(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .AddArticleViews()
            .AddRendering(layout.RenderArticle));


    private static HtmlControl RenderArticle(this LayoutDefinition host, object obj)
    {
        if (obj is not Article article)
            return null;

        return new HtmlControl(article.PrerenderedHtml).AddSkin(article.MapToSkin());
    }

    private static ArticleSkin MapToSkin(this Article article)
    {
        return new ArticleSkin
        {
            Name = article.Name,
            Collection = article.Collection,
            Title = article.Title,
            Abstract = article.Abstract,
            Authors = article.Authors,
            Published = article.Published,
            Tags = article.Tags,
        };
    }
    internal static LayoutDefinition AddArticleViews(this LayoutDefinition layout)
        => layout.WithView(nameof(Article), Article)
            .WithView(nameof(Catalog), Catalog);

    private static IObservable<object> Article(LayoutAreaHost host, RenderingContext ctx)
    {
        var collection = host.Hub.Address.GetCollectionName();
        var source = host.Hub.GetCollection(collection);
        return source.GetArticle(host.Reference.Id.ToString());
    }

    private static IObservable<object> Catalog(LayoutAreaHost host, RenderingContext ctx)
    {
        var collectionName = host.Hub.Address.GetCollectionName();
        var collection = host.Hub.GetCollection(collectionName);
        return 
            collection.GetArticles(ParseToOptions(host.Reference))
            .Select(x => x.Aggregate(Controls.Stack, (s,a) => s.WithView(CreateControl(a))))
            

            ;
    }

    private static ArticleCatalogItemControl CreateControl(Article a) => 
        new(a with{Content = null, PrerenderedHtml = null});

    private static ArticleCatalogOptions ParseToOptions(LayoutAreaReference reference)
    {
        // TODO V10: Need to create some link from layout area reference id to options ==> url parsing. (24.01.2025, Roland Bürgi)
        return new();
    }


    public static string ArticleUrl(string collection, string path)
        => $"{ArticleAddress.TypeName}/{collection}/{path}";



    public static NavMenuControl ArticlesNavMenu(this NavMenuControl menu, string collection, string displayName = null)
        => menu.WithView(Controls.NavLink(displayName ?? collection.Wordify(), $"{ArticleAddress.TypeName}/{nameof(Catalog)}/{collection}"));

    public static LayoutAreaReference GetArticleLayoutReference(string path, Func<ArticleOptions, ArticleOptions> options = null)
        => new(nameof(Article)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)
    public static LayoutAreaReference GetCatalogLayoutReference(string path, Func<ArticleCatalogOptions, ArticleCatalogOptions> options = null)
        => new(nameof(Catalog)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)


}
