using System.Collections.Immutable;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MarkdownExtensions = MeshWeaver.Markdown.MarkdownExtensions;

namespace MeshWeaver.Articles;

public static class ArticleExtensions
{

    public static LayoutDefinition AddArticleLayouts(this LayoutDefinition layout)
    {
        return layout
            .WithView(nameof(ArticleLayoutArea.Article), ArticleLayoutArea.Article)
            .WithView(nameof(ArticleCatalogLayoutArea.Catalog), ArticleCatalogLayoutArea.Catalog);
    }




    internal static IArticleService GetArticleService(this IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<IArticleService>();

    public static IServiceCollection AddArticles(this IServiceCollection services)
        => services
                .AddSingleton<IArticleService, ArticleService>()
                .AddKeyedSingleton<IArticleCollectionFactory, FileSystemArticleCollectionFactory>(FileSystemArticleCollectionFactory.SourceType)
            ;
   


    public static Article ParseArticle(string collection, string path, DateTime lastWriteTime, string content,
        IReadOnlyDictionary<string, Author> authors)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\\", "/");

        var pipeline = MarkdownExtensions.CreateMarkdownPipeline(collection);
        var document = Markdig.Markdown.Parse(content, pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        var name = Path.GetFileNameWithoutExtension(path);

        var ret = yamlBlock is null
            ? new Article()
            : new YamlDotNet.Serialization.DeserializerBuilder().Build()
                .Deserialize<Article>(yamlBlock.Lines.ToString());

        return ret with
            {
                Name = name,
                Path = path,
                Collection = collection,
                Url = GetArticleUrl(collection, name),
                Extension = Path.GetExtension(path),
                PrerenderedHtml = document.ToHtml(pipeline),
                LastUpdated = lastWriteTime,
                Content = content,
                CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray(),
                AuthorDetails = ret.Authors.Select(x => authors.GetValueOrDefault(x) ?? ConvertToAuthor(x)).ToArray()
            };
    }

    private static Author ConvertToAuthor(string authorName)
    {
        var names = authorName.Split(' ');
        if (names.Length == 1)
            return new Author(string.Empty, names[0]);
        if (names.Length == 2)
            return new Author(names[0], names[1]);
        return new Author(names[0], names[^1]) { MiddleName = string.Join(' ', names.Skip(1).Take(names.Length - 2)), };
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


