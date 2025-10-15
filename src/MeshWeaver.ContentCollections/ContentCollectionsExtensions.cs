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
    /// <summary>
    /// Gets the localized collection name for a hub by appending the hub's address ID.
    /// </summary>
    /// <param name="collectionName">The base collection name</param>
    /// <param name="addressId">The hub's address ID</param>
    /// <returns>The localized collection name in format: {collectionName}-{addressId}</returns>
    public static string GetLocalizedCollectionName(string collectionName, string addressId)
        => $"{collectionName}-{addressId}";

    public static MessageHubConfiguration AddArticles(
        this MessageHubConfiguration config,
        params IReadOnlyCollection<ContentCollectionConfig> configurations)
    {
        return config
            .WithTypes(typeof(Article), typeof(ArticleControl), typeof(ArticleCatalogItemControl), typeof(ArticleCatalogControl), typeof(ArticleCatalogSkin))
            .WithServices(services =>
            services.AddScoped(sp => sp.GetConfiguration(config, configurations))
            )
            .AddLayout(layout => layout
                .WithView(nameof(ArticlesLayoutArea.Articles), ArticlesLayoutArea.Articles))
            .AddContentCollections();
    }

    private static ArticlesConfiguration GetConfiguration(this IServiceProvider serviceProvider, MessageHubConfiguration config, IReadOnlyCollection<ContentCollectionConfig> configurations)
    {
        var hub = serviceProvider.GetRequiredService<IMessageHub>();
        if (configurations.Count == 0)
        {
            var contentService = hub.GetContentService();
            var collection = contentService.GetCollectionConfig(config.Address.Id);
            if (collection is null)
                throw new InvalidOperationException($"No content collection configured for hub at address '{config.Address.Id}'. Ensure a content collection is registered for this hub.");
            return new ArticlesConfiguration() { CollectionConfigurations = [collection] };

        }

        return new ArticlesConfiguration()
        {
            CollectionConfigurations = configurations,
            Collections = configurations.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray()
        };
    }

    public static MessageHubConfiguration AddContentCollections(this MessageHubConfiguration config, IConfiguration? configuration = null, string? collectionsConfigKey = null)
    {
        return config
            .WithServices(services =>
            {
                // Register the ContentCollectionRegistry at hub level
                // Does NOT delegate to parent - parent delegation is handled by ContentService
                services.AddScoped<IContentCollectionRegistry>(_ =>
                {
                    var registry = new ContentCollectionRegistry();

                    // Load collections from configuration if provided
                    if (configuration != null && !string.IsNullOrEmpty(collectionsConfigKey))
                    {
                        var collectionSections = configuration.GetSection(collectionsConfigKey).GetChildren();
                        foreach (var section in collectionSections)
                        {
                            var collectionName = section.Key;
                            var collectionConfig = section.Get<ContentCollectionConfig>();
                            if (collectionConfig != null)
                            {
                                // Set the name from the section key if not specified
                                if (string.IsNullOrEmpty(collectionConfig.Name))
                                    collectionConfig.Name = collectionName;

                                // Set the address to this hub's address
                                collectionConfig.Address = config.Address;

                                // Register in the registry with lazy provider factory
                                registry.WithCollection(collectionName, new ContentCollectionRegistration(
                                    collectionConfig,
                                    _ => CreateStreamProvider(collectionConfig)
                                ));
                            }
                        }
                    }

                    return registry;
                });

                // Register the content service and factories
                services.AddContentCollections(configuration, collectionsConfigKey);

                return services;
            })
            .AddLayout(layout => layout
                .WithView(nameof(ContentLayoutArea.Content), ContentLayoutArea.Content)
                .WithView(nameof(FileBrowserLayoutAreas.FileBrowser), FileBrowserLayoutAreas.FileBrowser))
            .WithHandler<GetContentCollectionRequest>((hub, request) =>
            {
                var response = GetContentCollectionResponse(hub, request);
                hub.Post(response, o => o.ResponseFor(request));
                return request.Processed();
            });
    }

    private static IStreamProvider CreateStreamProvider(ContentCollectionConfig config)
    {
        var sourceType = config.SourceType;
        return sourceType switch
        {
            "FileSystem" => new FileSystemStreamProvider(config.BasePath ?? ""),
            "EmbeddedResource" => throw new NotSupportedException("EmbeddedResource requires assembly and prefix - use AddEmbeddedResourceContentCollection"),
            _ => throw new NotSupportedException($"SourceType '{sourceType}' is not supported")
        };
    }

    internal static IContentService GetContentService(this IMessageHub hub)
        => hub.ServiceProvider.GetRequiredService<IContentService>();


    private static GetContentCollectionResponse GetContentCollectionResponse(
        IMessageHub hub,
        IMessageDelivery<GetContentCollectionRequest> delivery)
    {
        var contentService = hub.GetContentService();

        if (delivery.Message.CollectionNames is null || delivery.Message.CollectionNames.Count == 0)
            return new();

        var configs = delivery.Message.CollectionNames
            .Select(c => contentService.GetCollectionConfig(c))
            .OfType<ContentCollectionConfig>()
            .ToArray();

        return new GetContentCollectionResponse
        {
            Collections = configs
        };
    }


    public static IServiceCollection AddContentCollections(this IServiceCollection services, IConfiguration? configuration = null, string? collectionsConfigKey = null)
    {
        // Only register core services if they haven't been registered yet
        if (services.All(d => d.ServiceType != typeof(IContentService)))
        {
            services
                .AddScoped<IContentService, ContentService>()
                .AddKeyedScoped<IContentCollectionFactory, FileSystemContentCollectionFactory>(FileSystemContentCollectionFactory.SourceType)
                .AddKeyedScoped<IContentCollectionFactory, HubContentCollectionFactory>(HubContentCollectionFactory.SourceType)
                .AddKeyedScoped<IContentCollectionFactory, EmbeddedResourceContentCollectionFactory>(EmbeddedResourceContentCollectionFactory.SourceType)
                .AddKeyedScoped<IStreamProviderFactory, FileSystemStreamProviderFactory>("FileSystem")
                .AddKeyedScoped<IStreamProviderFactory, EmbeddedResourceStreamProviderFactory>("EmbeddedResource");
        }

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

            // Register content collections from configuration if specified
            if (!string.IsNullOrEmpty(collectionsConfigKey))
            {
                var collectionSections = configuration.GetSection(collectionsConfigKey).GetChildren();
                foreach (var section in collectionSections)
                {
                    var collectionName = section.Key;
                    var collectionConfig = section.Get<ContentCollectionConfig>();
                    if (collectionConfig != null)
                    {
                        // Set the name from the section key if not specified
                        if (string.IsNullOrEmpty(collectionConfig.Name))
                            collectionConfig.Name = collectionName;

                        // Register as a named option or similar pattern if needed
                        // For now, this just validates the configuration exists
                    }
                }
            }
        }

        return services;
    }

    private static void AddStreamProvider(this IServiceCollection services, StreamProviderConfiguration config)
    {
        switch (config.ProviderType)
        {
            case "FileSystem":
                services.AddKeyedSingleton<IStreamProvider>(config.Name, (_, _) =>
                {
                    var basePath = config.Settings.GetValueOrDefault("BasePath", "");
                    return new FileSystemStreamProvider(basePath);
                });
                break;

            case "AzureBlob":
                services.AddKeyedSingleton<IStreamProvider>(config.Name, (sp, _) =>
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
        IReadOnlyDictionary<string, Author> authors, Address? address)
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
                Url = GetContentUrl(collection, pathWithoutExtension, address),
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

        // Adapt thumbnail URL to include address and collection path
        var adaptedThumbnail = AdaptResourceUrl(ret.Thumbnail, collection, address);

        // Render abstract as HTML if it exists
        var abstractHtml = string.IsNullOrEmpty(ret.Abstract)
            ? string.Empty
            : Markdig.Markdown.ToHtml(ret.Abstract, pipeline);

        return ret with
        {
            Name = name,
            Path = path,
            Collection = collection,
            Url = GetContentUrl(collection, pathWithoutExtension, address),
            PrerenderedHtml = document.ToHtml(pipeline),
            LastUpdated = lastWriteTime,
            Content = contentWithoutYaml,
            CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray()!,
            AuthorDetails = ret.Authors?.Select(x => authors.GetValueOrDefault(x) ?? ConvertToAuthor(x)).ToArray() ?? [],
            Thumbnail = adaptedThumbnail,
            AbstractHtml = abstractHtml
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

    public static string? AdaptResourceUrl(string? resourceUrl, string collection, Address? address)
    {
        if (string.IsNullOrEmpty(resourceUrl))
            return resourceUrl;

        // If it's already an absolute URL (http/https), use it as-is
        if (resourceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            resourceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return resourceUrl;
        }

        // If it starts with /, it's already a full path
        if (resourceUrl.StartsWith("/"))
        {
            return resourceUrl;
        }

        // Otherwise, prepend with address/static/collection or static/collection
        return address != null
            ? $"{address}/static/{collection}/{resourceUrl}"
            : $"static/{collection}/{resourceUrl}";
    }


    public static string GetContentUrl(string collection, string path, Address? address = null)
        => address != null
            ? $"{address}/Content/{collection}/{path}"
            : $"Content/{collection}/{path}";


    public static MessageHubConfiguration AddEmbeddedResourceContentCollection(
        this MessageHubConfiguration configuration,
        string collectionName,
        Assembly assembly,
        string relativePath)
        => configuration.WithServices(services =>
        {
            var resourcePrefix = $"{assembly.GetName().Name}.{relativePath}";

            // Register the stream provider for this embedded resource collection
            services.AddKeyedSingleton<IStreamProvider>(collectionName, (_, _) =>
                new EmbeddedResourceStreamProvider(assembly, resourcePrefix));

            // Register the content collection provider
            services.AddScoped<IContentCollectionConfigProvider>(_ =>
            {
                var config = new ContentCollectionConfig
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
                return new ContentCollectionConfigProvider(config);
            });

            return services;
        });

    /// <summary>
    /// Configures a FileSystem content collection for this hub with a dynamically calculated path.
    /// </summary>
    /// <param name="configuration">The message hub configuration</param>
    /// <param name="collectionName">The name of the collection</param>
    /// <param name="pathFactory">Factory function to compute the path based on service provider</param>
    /// <returns>The configured message hub configuration</returns>
    public static MessageHubConfiguration AddFileSystemContentCollection(
        this MessageHubConfiguration configuration,
        string collectionName,
        Func<IServiceProvider, string> pathFactory)
        => configuration
            .AddContentCollections()
            .WithServices(services =>
            {
                // Register the content collection provider
                services.AddScoped<IContentCollectionConfigProvider>(sp =>
                {
                    var basePath = pathFactory(sp);

                    var config = new ContentCollectionConfig
                    {
                        Name = collectionName,
                        SourceType = "FileSystem",
                        BasePath = basePath,
                        Address = configuration.Address,
                        Settings = new Dictionary<string, string>
                        {
                            ["BasePath"] = basePath
                        }
                    };

                    return new ContentCollectionConfigProvider(config);
                });

                return services;
            });

    /// <summary>
    /// Configures a content collection for this hub using the provided configuration factory.
    /// Uses the ContentCollectionRegistry to register the collection hierarchically.
    /// </summary>
    /// <param name="configuration">The message hub configuration</param>
    /// <param name="collectionConfigFactory">Factory function that creates the content collection configuration</param>
    /// <returns>The configured message hub configuration</returns>
    public static MessageHubConfiguration AddContentCollection(
        this MessageHubConfiguration configuration,
        Func<IServiceProvider, ContentCollectionConfig> collectionConfigFactory)
        => configuration
            .AddContentCollections() // Ensure content service and registry are initialized
            .WithServices(services =>
            {
                // Register the content collection provider
                services.AddScoped<IContentCollectionConfigProvider>(sp =>
                {
                    var collectionConfig = collectionConfigFactory(sp);

                    // Ensure Settings are set
                    var settings = collectionConfig.Settings ?? new Dictionary<string, string>();
                    if (collectionConfig.BasePath != null && !settings.ContainsKey("BasePath"))
                    {
                        settings["BasePath"] = collectionConfig.BasePath;
                    }

                    var finalConfig = collectionConfig with
                    {
                        Address = configuration.Address,
                        Settings = settings
                    };

                    return new ContentCollectionConfigProvider(finalConfig);
                });

                return services;
            });
    /// <summary>
    /// Configures a content collection for this hub using the provided configuration factory.
    /// Uses the ContentCollectionRegistry to register the collection hierarchically.
    /// </summary>
    /// <param name="configuration">The message hub configuration</param>
    /// <param name="collectionConfigs">Factory function that creates the content collection configuration</param>
    /// <returns>The configured message hub configuration</returns>
    public static MessageHubConfiguration AddContentCollections(
        this MessageHubConfiguration configuration,
        params IReadOnlyCollection<ContentCollectionConfig> collectionConfigs)
        => configuration
            .AddContentCollections() // Ensure content service and registry are initialized
            .WithServices(services =>
            {
                // Register the content collection provider
                services.AddScoped<IContentCollectionConfigProvider>(_ => new ContentCollectionConfigProvider(collectionConfigs));

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


