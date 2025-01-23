using System.Collections.Immutable;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
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
            .AddArticleViews()
            .WithInitialization((hub,ct) => GetCollection(hub,collection)?.InitializeAsync(ct) 
                                            ?? throw new ArgumentException($"Misconfigured article collection {collection}"));
    }


    internal static string GetCollectionName(this Address address)
    {
        var collection = (address as ArticleAddress)?.Id;
        if (collection is null)
            throw new ArgumentException(
                $"Expected address to be of type ArticleAddress. But was: {address.GetType().Name}");
        return collection;
    }


    internal static ArticleCollection GetCollection(this IMessageHub hub, string collection)
        => hub.ServiceProvider.GetRequiredService<IArticleService>().GetCollection(collection);

    public static MeshBuilder AddArticles(this MeshBuilder builder,Func<ArticleConfiguration, ArticleConfiguration> articles) 
        => builder
            .ConfigureMesh(config =>
            config
                .AddMeshNodeFactory()
                .Set(config.GetListOfLambdas().Add(articles))
            )
            .ConfigureHub(config => config.WithServices(services => services.AddSingleton<IArticleService, ArticleService>()));

    private static MeshConfiguration AddMeshNodeFactory(this MeshConfiguration config)
        => config.AddMeshNodeFactory((type, id) => type == ArticleAddress.TypeName
            ? new MeshNode(type, id, "Articles", "Articles") { HubConfiguration = ConfigureArticleHub }
            : null
        );


    internal static ImmutableList<Func<ArticleConfiguration, ArticleConfiguration>> GetListOfLambdas(this MeshConfiguration configuration)
        => configuration.Get<ImmutableList<Func<ArticleConfiguration, ArticleConfiguration>>>() ?? [];


    public static MeshArticle ParseArticle(string collection, string path, string content)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\\", "/");

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection);
        var document = Markdig.Markdown.Parse(content, pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (yamlBlock is null)
            return SetStandardProperties(new(), collection, path, document, pipeline);

        var yaml = yamlBlock.Lines.ToString();
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        var ret = deserializer.Deserialize<MeshArticle>(yaml);
        return SetStandardProperties(ret, collection, path, document, pipeline);
    }

    private static MeshArticle SetStandardProperties(MeshArticle ret, string collection, string path, MarkdownDocument document, MarkdownPipeline pipeline)
    {
        return ret with
        {
            Name = ret.Name ?? Path.GetFileNameWithoutExtension(path),
            Path = path,
            Collection = collection,
            Url = $"{ArticleAddress.TypeName}/{collection}/{path}",
            Extension = Path.GetExtension(path),
            PrerenderedHtml = document.ToHtml(pipeline)
        };
    }



}

public record ArticleCatalogOptions
{
    internal ImmutableHashSet<string> Tags { get; init; }= [];

    public ArticleCatalogOptions WithTag(string northwind)
        => this with { Tags = Tags.Add(northwind) };
}


