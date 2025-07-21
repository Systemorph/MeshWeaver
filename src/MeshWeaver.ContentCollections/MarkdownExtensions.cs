#nullable enable
using System.Text;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

public static class MarkdownExtensions
{
    public static MessageHubConfiguration AddArticles(this MessageHubConfiguration config) =>
        config
            .WithTypes(typeof(Article), typeof(ArticleControl), typeof(ArticleCatalogItemControl), typeof(ArticleCatalogSkin))
            .AddLayout(layout => layout
                .WithView(nameof(ContentLayoutArea.Content), ContentLayoutArea.Content)
                .WithView(nameof(ArticleCatalogLayoutArea.Catalog), ArticleCatalogLayoutArea.Catalog));

    internal static IContentService GetContentService(this IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<IContentService>();

    public static IServiceCollection AddContentCollections(this IServiceCollection services)
        => services
                .AddSingleton<IContentService, ContentService>()
                .AddKeyedSingleton<IContentCollectionFactory, FileSystemContentCollectionFactory>(FileSystemContentCollectionFactory.SourceType)
            ;

    public static string ConvertToMarkdown(this Article article)
    {
        var markdownBuilder = new StringBuilder();
        markdownBuilder.AppendLine("---");
        markdownBuilder.AppendLine($"Title: \"{article.Title?.Replace("\"", "\\\"")}\"");

        if (!string.IsNullOrEmpty(article.Abstract))
        {
            markdownBuilder.AppendLine("Abstract: >");
            foreach (var line in article.Abstract.Split('\n'))
            {
                markdownBuilder.AppendLine($"  {line.TrimEnd()}");
            }
        }

        if (!string.IsNullOrEmpty(article.Thumbnail))
            markdownBuilder.AppendLine($"Thumbnail: \"{article.Thumbnail}\"");

        if (!string.IsNullOrEmpty(article.VideoUrl))
            markdownBuilder.AppendLine($"VideoUrl: \"{article.VideoUrl}\"");

        if (article.VideoDuration != default)
            markdownBuilder.AppendLine($"VideoDuration: \"{article.VideoDuration:hh\\:mm\\:ss}\"");

        if (!string.IsNullOrEmpty(article.VideoTitle))
            markdownBuilder.AppendLine($"VideoTitle: \"{article.VideoTitle?.Replace("\"", "\\\"")}\"");

        if (!string.IsNullOrEmpty(article.VideoTagLine))
            markdownBuilder.AppendLine($"VideoTagLine: \"{article.VideoTagLine?.Replace("\"", "\\\"")}\"");

        if (!string.IsNullOrEmpty(article.VideoTranscript))
            markdownBuilder.AppendLine($"VideoTranscript: \"{article.VideoTranscript}\"");

        if (article.Published.HasValue)
            markdownBuilder.AppendLine($"Published: \"{article.Published.Value:yyyy-MM-dd}\"");

        if (article.Authors.Count > 0)
        {
            markdownBuilder.AppendLine("Authors:");
            foreach (var author in article.Authors)
            {
                markdownBuilder.AppendLine($"  - \"{author}\"");
            }
        }

        if (article.Tags.Count > 0)
        {
            markdownBuilder.AppendLine("Tags:");
            foreach (var tag in article.Tags)
            {
                markdownBuilder.AppendLine($"  - \"{tag}\"");
            }
        }

        markdownBuilder.AppendLine("---");
        markdownBuilder.AppendLine();
        if (string.IsNullOrEmpty(article.Content))
            return markdownBuilder.ToString();
        // Append the main content
        markdownBuilder.Append(article.Content);

        return markdownBuilder.ToString();
    }
    public static MarkdownElement ParseContent(string collection, string path, DateTime lastWriteTime, string content,
        IReadOnlyDictionary<string, Author> authors)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\\", "/");

        var pipeline = Markdown.MarkdownExtensions.CreateMarkdownPipeline(collection);
        var document = Markdig.Markdown.Parse(content, pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        var name = Path.GetFileNameWithoutExtension(path);
        var pathWithoutExtension = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

        if (yamlBlock is null)
            return new MarkdownElement
            {
                Name = name,
                Path = path,
                Collection = collection,
                Url = GetContentUrl(collection, pathWithoutExtension),
                PrerenderedHtml = document.ToHtml(pipeline),
                LastUpdated = lastWriteTime,
                Content = content,
                CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray()!,

            };

        Article ret;
        try
        {
            ret = new YamlDotNet.Serialization.DeserializerBuilder().Build()
                .Deserialize<Article>(yamlBlock.Lines.ToString());
        }
        catch
        {
            ret = new Article
            {
                Name = string.Empty,
                Collection = string.Empty,
                PrerenderedHtml = string.Empty,
                Content = string.Empty,
                Url = string.Empty,
                Path = string.Empty,
                CodeSubmissions = [],
                Title = string.Empty,
                Source = string.Empty
            };
        }

        // Remove the YAML block from the content
        var contentWithoutYaml = content.Substring(yamlBlock.Span.End + 1).Trim('\r', '\n');

        return ret with
        {
            Name = name,
            Path = path,
            Collection = collection,
            Url = GetContentUrl(collection, pathWithoutExtension),
            PrerenderedHtml = document.ToHtml(pipeline),
            LastUpdated = lastWriteTime,
            Content = contentWithoutYaml,
            CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray()!,
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


    public static string GetContentUrl(string collection, string path)
        => $"content/{collection}/{path}";



}

public record ArticleCatalogOptions
{
    public string Collection { get; init; } = string.Empty;
    public int Page { get; init; }
    public int PageSize { get; init; } = 10;

    public ArticleSortOrder SortOrder { get; init; }
}

public enum ArticleSortOrder
{
    DescendingPublishDate,
    AscendingPublishDate
}


