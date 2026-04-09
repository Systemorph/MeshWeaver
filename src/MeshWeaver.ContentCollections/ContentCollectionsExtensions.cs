using System.Reactive.Linq;
using System.Reflection;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using MeshWeaver.ContentCollections.Completion;
using MeshWeaver.Data;
using MeshWeaver.Data.Completion;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ContentCollections;

public static class ContentCollectionsExtensions
{
    /// <summary>
    /// Gets the localized collection name for a hub by appending the hub's address ID.
    /// </summary>
    /// <param name="collectionName">The base collection name</param>
    /// <param name="addressId">The hub's address ID</param>
    /// <returns>The localized collection name in format: {collectionName}@{addressId}</returns>
    public static string GetLocalizedCollectionName(string collectionName, string addressId)
        => $"{collectionName}@{addressId}";

    /// <summary>
    /// Default collection name used when no specific collection is configured or specified.
    /// </summary>
    public const string DefaultCollectionName = "content";

    /// <summary>
    /// Area name for unified content references. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string ContentAreaName = "$Content";

    /// <summary>
    /// Area name for collection configuration. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string CollectionAreaName = "$Collection";

    /// <summary>
    /// Area name for file browser. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string FileBrowserAreaName = "$FileBrowser";

    extension(MessageHubConfiguration config)
    {
        /// <summary>
        /// Adds content collection infrastructure without registering layout areas.
        /// Use this at the mesh level to set up the content service and base storage collections.
        /// Node hubs should call AddContentCollections() which includes layout area registration.
        /// </summary>
        public MessageHubConfiguration AddContentCollectionsInfrastructure()
        {
            return config
                .WithTypes(typeof(ContentCollectionReference))
                .WithServices(AddContentService)
                .AddData(data =>
                {
                    // Register the content: prefix resolver for UnifiedReference (only if not already registered)
                    // This handles paths like "content:addressType/addressId/collection/path"
                    if (!data.UnifiedReferenceResolvers.ContainsKey("content"))
                    {
                        data = data.WithUnifiedReference("content", CreateContentPathStream);
                    }

                    // Register the collection: prefix resolver for UnifiedReference
                    // This handles paths like "collection:collectionName" or just "collection"
                    if (!data.UnifiedReferenceResolvers.ContainsKey("collection"))
                    {
                        data = data.WithUnifiedReference("collection", CreateCollectionConfigStream);
                    }

                    return data.Configure(reduction => reduction
                        .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                            reference is not FileReference fileRef
                                ? null
                                : CreateFileReferenceStream(workspace, fileRef, configuration))
                        .AddWorkspaceReferenceStream<object>((workspace, reference, configuration) =>
                            reference is not ContentCollectionReference
                                ? null
                                : CreateContentCollectionReferenceStream(workspace, reference, configuration)));
                })
                .WithHandler<GetDataRequest>(HandleCollectionConfigRequest);
        }

        /// <summary>
        /// Adds content collection support including layout areas ($Content, $FileBrowser, $Collection).
        /// Call this at node hub level where content collections are actually mapped.
        /// For mesh-level infrastructure only, use AddContentCollectionsInfrastructure().
        /// All area names use $ prefix to avoid conflicts with local view areas.
        /// </summary>
        public MessageHubConfiguration AddContentCollections()
        {
            if (config.Get<bool>(nameof(AddContentCollections)))
                return config;
            config = config.Set(true, nameof(AddContentCollections));
            return config
                .AddContentCollectionsInfrastructure()
                .AddLayout(layout => layout
                    .WithView(ContentAreaName, ContentLayoutArea.UnifiedContent)
                    .WithView(FileBrowserAreaName, FileBrowserLayoutAreas.FileBrowser)
                    .WithView(CollectionAreaName, CollectionLayoutArea.Collection));
        }
    }

    /// <summary>
    /// Creates a stream for collection: unified reference paths.
    /// Path format: collection or collection/name1,name2
    /// </summary>
    private static ISynchronizationStream<object>? CreateCollectionConfigStream(
        IWorkspace workspace,
        string? remainingPath)
    {
        // Parse collection names from path (comma-separated if multiple)
        string[]? collectionNames = null;
        if (!string.IsNullOrEmpty(remainingPath))
        {
            collectionNames = remainingPath.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        var reference = new ContentCollectionReference(collectionNames);
        return CreateContentCollectionReferenceStream(workspace, reference, null);
    }

    /// <summary>
    /// Creates a stream for a ContentCollectionReference.
    /// Returns collection configurations as a one-time observable.
    /// </summary>
    private static ISynchronizationStream<object>? CreateContentCollectionReferenceStream(
        IWorkspace workspace,
        WorkspaceReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        if (reference is not ContentCollectionReference collectionRef)
            return null;

        var hub = workspace.Hub;
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        if (contentService == null)
            return null;

        var streamIdentity = new StreamIdentity(hub.Address, collectionRef.ToString());
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            hub,
            collectionRef,
            workspace.ReduceManager.ReduceTo<object>(),
            configuration ?? (c => c)
        );

        // Create an observable that returns the collection configs
        var observable = Observable.FromAsync(ct =>
        {
            IReadOnlyCollection<ContentCollectionConfig> configs;
            var collectionNames = collectionRef.CollectionNames;

            if (collectionNames is null || collectionNames.Count == 0)
            {
                // Return all collection configurations
                configs = contentService.GetAllCollectionConfigs();
            }
            else
            {
                // Return specific collection configurations
                configs = collectionNames
                    .Select(contentService.GetCollectionConfig)
                    .OfType<ContentCollectionConfig>()
                    .ToArray();
            }

            return Task.FromResult<object?>(configs);
        });

        stream.RegisterForDisposal(
            observable
                .Select(value => new ChangeItem<object>(value!, stream.StreamId, hub.Version))
                .Where(x => x.Value != null)
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    /// <summary>
    /// Handles GetDataRequest for ContentCollectionReference or UnifiedReference with "collection:" prefix.
    /// Returns collection configurations via GetDataResponse.
    /// </summary>
    private static IMessageDelivery HandleCollectionConfigRequest(
        IMessageHub hub,
        IMessageDelivery<GetDataRequest> request)
    {
        // Handle both ContentCollectionReference and UnifiedReference with "collection:" prefix
        IReadOnlyCollection<string>? collectionNames = null;

        if (request.Message.Reference is ContentCollectionReference collectionRef)
        {
            collectionNames = collectionRef.CollectionNames;
        }
        else if (request.Message.Reference is UnifiedReference unifiedRef &&
                 unifiedRef.Path.StartsWith("collection:", StringComparison.OrdinalIgnoreCase))
        {
            // Parse collection names from "collection:name1,name2" format
            var remainingPath = unifiedRef.Path["collection:".Length..];
            if (!string.IsNullOrEmpty(remainingPath))
            {
                collectionNames = remainingPath.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        else
        {
            // Not a collection config request, let other handlers process it
            return request;
        }

        var contentService = hub.GetContentService();

        IReadOnlyCollection<ContentCollectionConfig> configs;
        if (collectionNames is null || collectionNames.Count == 0)
        {
            // Return all collection configurations
            configs = contentService.GetAllCollectionConfigs();
        }
        else
        {
            // Return specific collection configurations
            configs = collectionNames
                .Select(contentService.GetCollectionConfig)
                .OfType<ContentCollectionConfig>()
                .ToArray();
        }

        hub.Post(new GetDataResponse(configs, hub.Version), o => o.ResponseFor(request));
        return request.Processed();
    }

    /// <summary>
    /// Creates a stream for content: unified reference paths.
    /// Path format:
    ///   - path (uses default collection)
    ///   - collection/path
    ///   - collection@partition/path
    /// </summary>
    private static ISynchronizationStream<object>? CreateContentPathStream(
        IWorkspace workspace,
        string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return null;

        // remainingPath format: collection/path or collection@partition/path
        // If no slash, use the default collection name
        var slashIndex = remainingPath.IndexOf('/');
        string collectionPart;
        string filePath;

        if (slashIndex < 0)
        {
            // No slash - use default collection
            collectionPart = DefaultCollectionName;
            filePath = remainingPath;
        }
        else
        {
            collectionPart = remainingPath[..slashIndex];
            filePath = remainingPath[(slashIndex + 1)..];
        }

        if (string.IsNullOrEmpty(filePath))
            return null;

        // Check for partition
        var atIndex = collectionPart.IndexOf('@');
        if (atIndex > 0)
        {
            var collection = collectionPart[..atIndex];
            var partition = collectionPart[(atIndex + 1)..];
            return workspace.GetStream(new FileReference(collection, filePath, partition));
        }

        return workspace.GetStream(new FileReference(collectionPart, filePath), null);
    }

    /// <summary>
    /// Creates a stream for a FileReference by loading file content from the content service.
    /// </summary>
    private static ISynchronizationStream<object>? CreateFileReferenceStream(
        IWorkspace workspace,
        FileReference reference,
        Func<StreamConfiguration<object>, StreamConfiguration<object>>? configuration)
    {
        var fileContentProvider = workspace.Hub.ServiceProvider.GetService<Data.IFileContentProvider>();
        if (fileContentProvider == null)
            return null;

        var streamIdentity = new StreamIdentity(workspace.Hub.Address, reference.Path);
        var stream = new SynchronizationStream<object>(
            streamIdentity,
            workspace.Hub,
            reference,
            workspace.ReduceManager.ReduceTo<object>(),
            configuration ?? (c => c)
        );

        // Create an observable that loads the file content
        var observable = Observable.FromAsync(async ct =>
        {
            var result = await fileContentProvider.GetFileContentAsync(reference.Collection, reference.Path, null, ct);
            return result.Success ? (object?)result.Content : null;
        });

        stream.RegisterForDisposal(
            observable
                .Select(value => new ChangeItem<object>(value!, stream.StreamId, workspace.Hub.Version))
                .Where(x => x.Value != null)
                .DistinctUntilChanged()
                .Synchronize()
                .Subscribe(stream)
        );

        return stream;
    }

    public static IServiceCollection AddContentService(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(IContentService)))
        {
            services
                .AddScoped<IContentService, ContentService>()
                .AddScoped<Data.IFileContentProvider, FileContentProvider>()
                .AddSingleton<IContentTransformer, DocSharpContentTransformer>()
                .AddScoped<IAutocompleteProvider, ContentAutocompleteProvider>()
                .AddKeyedScoped<IStreamProviderFactory, FileSystemStreamProviderFactory>(FileSystemStreamProvider.SourceType)
                .AddKeyedScoped<IStreamProviderFactory, EmbeddedResourceStreamProviderFactory>(EmbeddedResourceStreamProvider.SourceType)
                .AddKeyedScoped<IStreamProviderFactory, HubStreamProviderFactory>(HubStreamProviderFactory.SourceType);
        }

        return services;
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

    public static MarkdownElement ParseContent(string collection, string path, DateTime lastWriteTime, string content, Address? address)
    {
        if (OperatingSystem.IsWindows())
            path = path.Replace("\\", "/");

        var pipeline = Markdown.MarkdownExtensions.CreateMarkdownPipeline(collection);
        var document = Markdig.Markdown.Parse(content, pipeline);
        var yamlBlock = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        var name = Path.GetFileNameWithoutExtension(path);
        var pathWithoutExtension = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

        // Remove the YAML block from the content if present
        var contentWithoutYaml = yamlBlock != null
            ? content.Substring(yamlBlock.Span.End + 1).Trim('\r', '\n')
            : content;

        // Return MarkdownElement - do not try to deserialize YAML as Article
        // Markdown files should be loaded as MeshNodes via IMeshStorage
        return new MarkdownElement
        {
            Name = name,
            Path = path,
            Collection = collection,
            Url = GetContentUrl(collection, pathWithoutExtension, address),
            PrerenderedHtml = document.ToHtml(pipeline),
            LastUpdated = lastWriteTime,
            Content = contentWithoutYaml,
            CodeSubmissions = document.Descendants().OfType<ExecutableCodeBlock>().Select(x => x.SubmitCode).Where(x => x is not null).ToArray()!,
        };
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

        // Prepend with /static/{address}/{collection} or /static/{collection}
        return address != null
            ? $"/static/{address}/{collection}/{resourceUrl}"
            : $"/static/{collection}/{resourceUrl}";
    }


    public static string GetContentUrl(string collection, string path, Address? address = null)
        => address != null
            ? $"/content/{address}/{collection}/{path}"
            : $"/content/{collection}/{path}";


    /// <param name="configuration">The message hub configuration</param>
    extension(MessageHubConfiguration configuration)
    {
        public MessageHubConfiguration AddEmbeddedResourceContentCollection(string collectionName,
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
                        IsEditable = false,
                        ExposeInChildren = false,
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
        /// <param name="collectionName">The name of the collection</param>
        /// <param name="pathFactory">Factory function to compute the path based on service provider</param>
        /// <returns>The configured message hub configuration</returns>
        public MessageHubConfiguration AddFileSystemContentCollection(string collectionName,
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
        /// <param name="collectionConfigFactory">Factory function that creates the content collection configuration</param>
        /// <returns>The configured message hub configuration</returns>
        public MessageHubConfiguration AddContentCollection(Func<IServiceProvider, ContentCollectionConfig> collectionConfigFactory)
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
        /// Registers content collection configurations without layout areas.
        /// Use this to register base storage collections at mesh level.
        /// For node hubs that need $Content layout area, call AddContentCollections() first.
        /// </summary>
        /// <param name="collectionConfigs">The collection configurations to register</param>
        /// <returns>The configured message hub configuration</returns>
        public MessageHubConfiguration AddContentCollections(params IReadOnlyCollection<ContentCollectionConfig> collectionConfigs)
            => configuration
                .AddContentCollectionsInfrastructure() // Infrastructure only, no layout areas
                .WithServices(services =>
                {
                    // Register the content collection provider
                    services.AddScoped<IContentCollectionConfigProvider>(_ => new ContentCollectionConfigProvider(collectionConfigs));

                    return services;
                });

        /// <summary>
        /// Maps a content collection from a registered source collection to a subdirectory path.
        /// The subdirectory is relative to the source collection's BasePath.
        /// Supports string interpolation for dynamic paths (e.g., $"persons/{config.Address.Segments.Last()}").
        /// Note: This only maps the collection. Call AddContentCollections() first if you need $Content layout area.
        /// </summary>
        /// <param name="targetCollectionName">The name of the mapped collection (e.g., "avatars")</param>
        /// <param name="sourceCollectionName">The name of the registered source collection (e.g., "storage")</param>
        /// <param name="subdirectory">The subdirectory within storage (e.g., "persons" or dynamic path)</param>
        /// <returns>The configured message hub configuration</returns>
        public MessageHubConfiguration MapContentCollection(string targetCollectionName,
            string sourceCollectionName,
            string subdirectory)
            => configuration
                .AddContentCollections() // Registers $Content, $FileBrowser, $Collection layout areas
                .WithServices(services =>
                {
                    // Register a lazy mapping provider that defers source lookup
                    // This avoids circular dependency during ContentService construction
                    services.AddScoped<IContentCollectionConfigProvider>(_ =>
                        new MappedContentCollectionConfigProvider(
                            targetCollectionName,
                            sourceCollectionName,
                            subdirectory,
                            configuration.Address));

                    return services;
                });
    }
}

/// <summary>
/// A content collection config provider for mapped collections.
/// Returns a placeholder config with SourceType="Mapped" that gets resolved lazily by ContentService.
/// This avoids circular dependencies during ContentService construction.
/// </summary>
internal class MappedContentCollectionConfigProvider(
    string targetCollectionName,
    string sourceCollectionName,
    string subdirectory,
    Address address)
    : IContentCollectionConfigProvider
{
    public const string MappedSourceType = "Mapped";
    public const string SourceCollectionKey = "SourceCollection";
    public const string SubdirectoryKey = "Subdirectory";

    private readonly ContentCollectionConfig _config = new()
    {
        Name = targetCollectionName,
        SourceType = MappedSourceType,
        Address = address,
        Settings = new Dictionary<string, string>
        {
            [SourceCollectionKey] = sourceCollectionName,
            [SubdirectoryKey] = subdirectory
        }
    };

    public IEnumerable<ContentCollectionConfig> GetCollections() => [_config];
}
