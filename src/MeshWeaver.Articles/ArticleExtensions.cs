using System.Collections.Immutable;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Articles;

public static class ArticleExtensions
{
    public static MessageHubConfiguration ConfigureArticleHub(this MessageHubConfiguration config)
    {
        var collection = GetCollectionName(config.Address);
        return config
            .AddLayout(layout => layout
                .WithView(nameof(ArticleLayoutArea.Article), ArticleLayoutArea.Article)
                .WithView(nameof(ArticleCatalogLayoutArea.Catalog), ArticleCatalogLayoutArea.Catalog)
            )
            .WithInitialization(hub =>
            {
                var coll = GetCollection(hub, collection);
                if (coll is null)
                    throw new ArgumentException($"Misconfigured article collection {collection}");
                coll.Initialize(hub);
            });
    }


    internal static string GetCollectionName(this Address address)
    {
        var collection = (address as ArticlesAddress)?.Id;
        if (collection is null)
            throw new ArgumentException(
                $"Expected address to be of type ArticleAddress. But was: {address.GetType().Name}");
        return collection;
    }


    internal static ArticleCollection GetCollection(this IMessageHub hub, string collection)
        => hub.ServiceProvider.GetRequiredService<IArticleService>().GetCollection(collection);

    public static MeshBuilder AddArticles(this MeshBuilder builder, Func<ArticleConfiguration, ArticleConfiguration> articles)
        => builder
            .ConfigureMesh(config =>
                config
                    .AddMeshNodeFactory()
                    .Set(config.GetListOfLambdas().Add(articles))
            )
            .ConfigureServices(services => services.AddSingleton<IArticleService, ArticleService>());
   

    private static MeshConfiguration AddMeshNodeFactory(this MeshConfiguration config)
        => config.AddMeshNodeFactory((type, id) => type == ArticlesAddress.TypeName
            ? new MeshNode(type, id, "Articles", "Articles") { HubConfiguration = ConfigureArticleHub }
            : null
        );


    internal static ImmutableList<Func<ArticleConfiguration, ArticleConfiguration>> GetListOfLambdas(this MeshConfiguration configuration)
        => configuration.Get<ImmutableList<Func<ArticleConfiguration, ArticleConfiguration>>>() ?? [];


    public static Article ParseArticle(string collection, string defaultAddress, string path, DateTime lastWriteTime, string content)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\\", "/");

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection, defaultAddress);
        var document = Markdig.Markdown.Parse(content, pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        var name = Path.GetFileNameWithoutExtension(path);

        return (
                yamlBlock is null
                    ? new Article()
                    : new YamlDotNet.Serialization.DeserializerBuilder().Build()
                        .Deserialize<Article>(yamlBlock.Lines.ToString())
            )
            with
            {
                Name = name,
                Path = path,
                Collection = collection,
                Url = GetArticleUrl(collection, name),
                Extension = Path.GetExtension(path),
                PrerenderedHtml = document.ToHtml(pipeline),
                LastUpdated = lastWriteTime,
                CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray()
            };
    }


    public static string GetArticleUrl(string collection, string path)
        => $"article/{collection}/{path}";



}

public record ArticleCatalogOptions
{
    internal ImmutableHashSet<string> Tags { get; init; }= [];

    public ArticleCatalogOptions WithTag(string northwind)
        => this with { Tags = Tags.Add(northwind) };
}


