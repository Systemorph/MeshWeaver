using System.Collections.Immutable;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Articles;

public record ArticleConfiguration(IMessageHub Hub)
{
    internal ImmutableDictionary<string, ArticleCollection> Collections { get; init; } = ImmutableDictionary<string, ArticleCollection>.Empty;
    public ArticleConfiguration WithCollection(ArticleCollection collection)
        => this with { Collections = Collections.Add(collection.Collection, collection) };

    public ArticleConfiguration AddArticles(params IEnumerable<ArticleSourceConfig> articles)
        => articles.Aggregate(this, (conf, coll) =>
            conf.WithCollection(coll.SourceType switch
            {
                FileArticleCollectionFactory.Files => Hub.CreateArticleCollection(coll),
                _ => throw new ArgumentException($"Unknown source type {coll.SourceType}")
            })
        );

    public ArticleConfiguration FromAppSettings()
    {
        var options =
            Hub.ServiceProvider.GetRequiredService<IOptions<List<ArticleSourceConfig>>>();
        return options is null ? this : AddArticles(options.Value);
    }
}

public class ArticleSourceConfig
{
    public string SourceType { get; set; } = FileArticleCollectionFactory.Files;
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string BasePath { get; set; }
}

public static class FileArticleCollectionFactory
{
    public const string Files = nameof(Files);
    public static ArticleCollection CreateArticleCollection(this IMessageHub hub, ArticleSourceConfig config) =>
        new FileSystemArticleCollection(config, hub);
}
