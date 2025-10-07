using System.Reflection;
using System.Text;
using Azure.Storage.Blobs;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

public static class ContentCollectionsExtensions
{
    public static MessageHubConfiguration AddArticles(
        this MessageHubConfiguration config,
        Func<ArticlesConfiguration, ArticlesConfiguration>? configure = null)
    {
        return config
            .WithTypes(typeof(Article), typeof(ArticleControl), typeof(ArticleCatalogItemControl), typeof(ArticleCatalogControl), typeof(ArticleCatalogSkin), typeof(GetContentRequest), typeof(GetContentResponse))
            .WithServices(services =>
            services.AddScoped<ArticlesConfiguration>(_ => configure is null ? new ArticlesConfiguration() { Addresses = [config.Address] } : configure.Invoke(new()))
            )
            .AddLayout(layout => layout
                .WithView(nameof(ArticlesLayoutArea.Articles), ArticlesLayoutArea.Articles)
                .WithView(nameof(ArticlesLayoutArea.Content), ArticlesLayoutArea.Content))
            .AddContentCollections();
    }
    public static MessageHubConfiguration AddContentCollections(this MessageHubConfiguration config, IConfiguration? configuration = null) =>
        config
            .WithServices(services => services.AddContentCollections(configuration))
            .WithTypes(typeof(GetContentRequest), typeof(GetContentResponse), typeof(GetContentCollectionRequest), typeof(GetContentCollectionResponse))
            .WithHandler<GetContentRequest>(async (hub, request, ct) =>
            {
                var response = await GetContentCollectionsResponse(hub, request, ct);
                hub.Post(response, o => o.ResponseFor(request));
                return request.Processed();
            })
            .WithHandler<GetContentCollectionRequest>(async (hub, request, ct) =>
            {
                var response = await GetContentCollectionResponse(hub, request, ct);
                hub.Post(response, o => o.ResponseFor(request));
                return request.Processed();
            });

    internal static IContentService GetContentService(this IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<IContentService>();

    private static async Task<GetContentResponse> GetContentCollectionsResponse(
        IMessageHub hub,
        IMessageDelivery<GetContentRequest> delivery,
        CancellationToken cancellationToken)
    {
        var request = delivery.Message;
        var contentService = hub.GetContentService();

        // Get the collection mapped to this address
        var contentCollection = contentService.GetCollection(request.Collection);

        if (contentCollection is null)
        {
            return new GetContentResponse(null, null);
        }

        // Delegate to the content collection to prepare the response
        return await contentCollection.GetContentResponseAsync(request.Path, cancellationToken);
    }

    private static async Task<GetContentCollectionResponse> GetContentCollectionResponse(
        IMessageHub hub,
        IMessageDelivery<GetContentCollectionRequest> _,
        CancellationToken cancellationToken)
    {
        var contentService = hub.GetContentService();

        // Get all collections for this hub
        var contentCollections = await contentService.GetCollectionsAsync(cancellationToken);

        if (contentCollections.Count == 0)
        {
            return new();
        }

        // Build configuration for each collection
        var configs = contentCollections
            .Where(c => c.Address is not null && c.Address.Type == hub.Address.Type && c.Address.Id == hub.Address.Id)
            .Select(contentCollection =>
        {
            var config = new Dictionary<string, string>();
            var providerType = contentCollection.Config.SourceType;

            // Copy settings from config if available
            if (contentCollection.Config.Settings != null)
            {
                foreach (var setting in contentCollection.Config.Settings)
                {
                    config[setting.Key] = setting.Value;
                }
            }

            // Add provider-specific configuration
            switch (providerType)
            {
                case "FileSystem":
                    if (contentCollection.Config.BasePath != null)
                    {
                        config["BasePath"] = contentCollection.Config.BasePath;
                    }
                    break;

                case "EmbeddedResource":
                    // For embedded resources, extract assembly name and resource prefix from provider
                    var embeddedProvider = hub.ServiceProvider.GetKeyedService<IStreamProvider>(contentCollection.Collection);
                    if (embeddedProvider is EmbeddedResourceStreamProvider)
                    {
                        // We need to expose these properties on the provider or store them in Settings
                        // For now, they should be in Settings
                    }
                    break;

                case "AzureBlob":
                    // Azure Blob settings should already be in Settings
                    break;
            }

            return new ContentCollectionConfig
            {
                ProviderType = providerType,
                CollectionName = contentCollection.Collection,
                Configuration = config
            };
        }).ToArray();

        return new GetContentCollectionResponse
        {
            Collections = configs
        };
    }


    private static IServiceCollection AddContentCollections(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services
            .AddSingleton<IContentService, ContentService>()
            .AddKeyedSingleton<IContentCollectionFactory, FileSystemContentCollectionFactory>(FileSystemContentCollectionFactory.SourceType)
            .AddKeyedSingleton<IStreamProviderFactory, FileSystemStreamProviderFactory>("FileSystem")
            .AddKeyedSingleton<IStreamProviderFactory, EmbeddedResourceStreamProviderFactory>("EmbeddedResource");

        // Register stream providers if configuration is provided
        if (configuration != null)
        {
            var config = configuration.GetSection("StreamProviders").Get<StreamProvidersConfiguration>();
            if (config?.Providers != null)
            {
                foreach (var providerConfig in config.Providers)
                {
                    services.AddStreamProvider(providerConfig);
                }
            }
        }

        return services;
    }

    private static IServiceCollection AddStreamProvider(this IServiceCollection services, StreamProviderConfiguration config)
    {
        switch (config.ProviderType)
        {
            case "FileSystem":
                services.AddKeyedSingleton<IStreamProvider>(config.Name, (sp, key) =>
                {
                    var basePath = config.Settings.GetValueOrDefault("BasePath", "");
                    return new FileSystemStreamProvider(basePath);
                });
                break;

            case "AzureBlob":
                services.AddKeyedSingleton<IStreamProvider>(config.Name, (sp, key) =>
                {
                    var factory = sp.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();
                    var clientName = config.Settings.GetValueOrDefault("ClientName", "default");
                    var containerName = config.Settings.GetValueOrDefault("ContainerName", config.Name);
                    var blobServiceClient = factory.CreateClient(clientName);
                    return new AzureBlobStreamProvider(blobServiceClient, containerName);
                });
                break;

            default:
                throw new InvalidOperationException($"Unknown stream provider type: {config.ProviderType}");
        }

        return services;
    }

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

        if (article.Authors?.Count > 0)
        {
            markdownBuilder.AppendLine("Authors:");
            foreach (var author in article.Authors)
            {
                markdownBuilder.AppendLine($"  - \"{author}\"");
            }
        }

        if (article.Tags?.Count > 0)
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
            AuthorDetails = ret.Authors?.Select(x => authors.GetValueOrDefault(x) ?? ConvertToAuthor(x)).ToArray() ?? []
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


    public static MessageHubConfiguration WithEmbeddedResourceContentCollection(
        this MessageHubConfiguration configuration,
        string collectionName,
        Assembly assembly,
        string relativePath)
        => configuration.WithServices(services =>
        {
            var resourcePrefix = $"{assembly.GetName().Name}.{relativePath}";

            // Register the stream provider for this embedded resource collection
            services.AddKeyedSingleton<IStreamProvider>(collectionName, (sp, key) =>
                new EmbeddedResourceStreamProvider(assembly, resourcePrefix));

            // Register the content collection provider
            services.AddSingleton<IContentCollectionProvider>(sp =>
            {
                var hub = sp.GetRequiredService<IMessageHub>();
                var provider = new EmbeddedResourceStreamProvider(assembly, resourcePrefix);
                var config = new ContentSourceConfig
                {
                    Name = collectionName,
                    SourceType = "EmbeddedResource",
                    Settings = new Dictionary<string, string>
                    {
                        ["AssemblyName"] = assembly.GetName().Name ?? "",
                        ["ResourcePrefix"] = resourcePrefix
                    },
                    Address = configuration.Address
                };
                return new ContentCollectionProvider(
                    new ContentCollection(config, provider, hub)
                );
            });

            return services;
        });


}

public record ArticleCatalogOptions
{
    public IReadOnlyCollection<string>? Collections { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; } = 10;

    public ArticleSortOrder SortOrder { get; init; }
}

public enum ArticleSortOrder
{
    DescendingPublishDate,
    AscendingPublishDate
}


