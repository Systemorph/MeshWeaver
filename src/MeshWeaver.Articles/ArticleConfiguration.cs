using System.Collections.Immutable;

namespace MeshWeaver.Articles;

public record ArticleConfiguration
{
    internal ImmutableDictionary<string, ArticleCollection> Collections { get; init; } = ImmutableDictionary<string, ArticleCollection>.Empty;
    public ArticleConfiguration WithCollection(ArticleCollection collection)
        => this with { Collections = Collections.Add(collection.Collection, collection) };
    internal ImmutableList<Func<ArticleOptions, Task<MeshArticle>>> ArticleStreamFactories { get; init; } = [];

    public ArticleConfiguration WithArticleStreamFactory(Func<ArticleOptions, Task<MeshArticle>> factory)
        => this with { ArticleStreamFactories = ArticleStreamFactories.Add(factory) };

}

