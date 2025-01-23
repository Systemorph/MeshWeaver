using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public static class ArticleLayouts
{
    internal static MessageHubConfiguration AddArticleViews(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout.AddArticleViews()
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
        throw new NotImplementedException();
    }
    public static string ArticleUrl(string source, string path)
        => $"{ArticleAddress.TypeName}/{source}/{path}";



    public static NavMenuControl ArticlesNavMenu(this NavMenuControl menu, string collection)
        => menu.WithView(new LayoutAreaControl($"{ArticleAddress.TypeName}/{collection}", new(nameof(Catalog))));

    public static LayoutAreaReference GetArticleLayoutReference(string path, Func<ArticleOptions, ArticleOptions> options = null)
        => new(nameof(Article)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)
    public static LayoutAreaReference GetCatalogLayoutReference(string path, Func<ArticleCatalogOptions, ArticleCatalogOptions> options = null)
        => new(nameof(Catalog)) { Id = path }; // TODO V10: Create sth with options (22.01.2025, Roland Bürgi)


}
