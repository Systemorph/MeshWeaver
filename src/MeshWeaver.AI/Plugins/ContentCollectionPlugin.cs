using System.ComponentModel;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Plugin for managing documents and files in content collections.
/// Provides functions for listing, loading, saving, and deleting documents.
///
/// When working with agent context, the typical URL format is:
/// area: 'Content', Id: '{collection}/{path}'
///
/// The plugin automatically parses collection and path from this format.
/// </summary>
public class ContentCollectionPlugin
{
    private readonly IContentService contentService;
    private readonly ContentCollectionPluginConfig config;
    private readonly IAgentChat chat;

    public ContentCollectionPlugin(IMessageHub hub, ContentCollectionPluginConfig config, IAgentChat chat)
    {
        this.config = config;
        this.chat = chat;
        contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        // Add all configured collections to IContentService
        foreach (var collectionConfig in config.Collections)
        {
            contentService.AddConfiguration(collectionConfig);
        }
    }

    /// <summary>
    /// Gets the default collection name from config, or returns the provided name if not null.
    /// Parses the context URL in format 'area: Content, Id: {collection}/{path}' to extract collection name.
    /// Uses ContextToConfigMap if available to dynamically create collection config from agent context.
    /// </summary>
    private string? GetCollectionName(string? collectionName)
    {
        if (!string.IsNullOrEmpty(collectionName))
            return collectionName;

        // Try to parse collection from chat context URL format: Content/{collection}/{path}
        if (chat.Context != null)
        {
            var contextId = chat.Context.ToString();
            if (!string.IsNullOrEmpty(contextId))
            {
                var parts = contextId.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var collectionFromContext = parts[0];

                    // Try to get collection from ContextToConfigMap if available
                    if (config.ContextToConfigMap != null)
                    {
                        var contextConfig = config.ContextToConfigMap(chat.Context);
                        if (contextConfig != null)
                        {
                            // Add the dynamically created config to IContentService
                            contentService.AddConfiguration(contextConfig);
                            return contextConfig.Name;
                        }
                    }

                    return collectionFromContext;
                }
            }
        }

        // Fall back to the first collection from config as default
        return config.Collections.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Parses the path from the chat context URL format: Content/{collection}/{path}
    /// Returns null if no path is present in the context.
    /// </summary>
    private string? GetPathFromContext()
    {
        if (chat.Context == null)
            return null;

        var contextId = chat.Context.ToString();
        if (string.IsNullOrEmpty(contextId))
            return null;

        var parts = contextId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            // Skip the collection name (first part) and join the rest as path
            return string.Join('/', parts.Skip(1));
        }

        return null;
    }

    [KernelFunction]
    [Description("Lists all available collections with their configurations.")]
    public Task<string> GetCollections()
    {
        try
        {
            if (config.Collections.Count == 0)
                return Task.FromResult("No collections configured.");

            var collectionList = config.Collections.Select(c => new
            {
                c.Name,
                DisplayName = c.DisplayName ?? c.Name,
                Address = c.Address?.ToString() ?? "No address",
                c.SourceType,
                BasePath = c.BasePath ?? "Not specified"
            }).ToList();

            var result = string.Join("\n", collectionList.Select(c =>
                $"- {c.DisplayName} (Name: {c.Name}, Type: {c.SourceType}, Path: {c.BasePath}, Address: {c.Address})"));

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error retrieving collections: {ex.Message}");
        }
    }

    [KernelFunction]
    [Description("Lists all files in a specified collection at a given path. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> ListFiles(
        [Description("The name of the collection to list files from. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        [Description("The directory path within the collection. Use '/' for root. Can be inferred from context.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedCollectionName = GetCollectionName(collectionName);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = path ?? GetPathFromContext() ?? "/";
            var files = await collection.GetFilesAsync(resolvedPath);
            if (!files.Any())
                return $"No files found at path '{resolvedPath}' in collection '{resolvedCollectionName}'.";

            return string.Join("\n", files.Select(f =>
                $"- {f.Name} (Path: {f.Path}, Modified: {f.LastModified:yyyy-MM-dd HH:mm:ss})"));
        }
        catch (Exception ex)
        {
            return $"Error listing files in collection at path '{path ?? GetPathFromContext() ?? "/"}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all folders in a specified collection at a given path. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> ListFolders(
        [Description("The name of the collection to list folders from. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        [Description("The directory path within the collection. Use '/' for root. Can be inferred from context.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedCollectionName = GetCollectionName(collectionName);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = path ?? GetPathFromContext() ?? "/";
            var folders = await collection.GetFoldersAsync(resolvedPath);
            if (!folders.Any())
                return $"No folders found at path '{resolvedPath}' in collection '{resolvedCollectionName}'.";

            return string.Join("\n", folders.Select(f =>
                $"- {f.Name} (Path: {f.Path})"));
        }
        catch (Exception ex)
        {
            return $"Error listing folders in collection at path '{path ?? GetPathFromContext() ?? "/"}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Lists all files and folders in a specified collection at a given path. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> ListCollectionItems(
        [Description("The name of the collection to list items from. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        [Description("The directory path within the collection. Use '/' for root. Can be inferred from context.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedCollectionName = GetCollectionName(collectionName);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = path ?? GetPathFromContext() ?? "/";
            var items = await collection.GetCollectionItemsAsync(resolvedPath);
            if (!items.Any())
                return $"No items found at path '{resolvedPath}' in collection '{resolvedCollectionName}'.";

            var folders = items.OfType<FolderItem>()
                .Select(f => $"[DIR]  {f.Name} (Path: {f.Path})");
            var files = items.OfType<FileItem>()
                .Select(f => $"[FILE] {f.Name} (Path: {f.Path})");

            return string.Join("\n", folders.Concat(files));
        }
        catch (Exception ex)
        {
            return $"Error listing items in collection at path '{path ?? GetPathFromContext() ?? "/"}': {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets the content of a specific document from a collection. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> GetDocument(
        [Description("The path to the document within the collection. Can be inferred from context URL 'Content/{collection}/{path}'.")] string? documentPath = null,
        [Description("The name of the collection containing the document. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedDocumentPath = documentPath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedDocumentPath))
            return "No document path specified and no path found in context.";

        try
        {
            var stream = await contentService.GetContentAsync(resolvedCollectionName, resolvedDocumentPath, cancellationToken);
            if (stream == null)
                return $"Document '{resolvedDocumentPath}' not found in collection '{resolvedCollectionName}'.";

            await using (stream)
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                return content;
            }
        }
        catch (FileNotFoundException)
        {
            return $"Document '{resolvedDocumentPath}' not found in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error reading document '{resolvedDocumentPath}' from collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Saves content as a document to a specified collection. When used with context area 'Content' and Id '{collection}/{path}', the collection can be automatically parsed from the context.")]
    public async Task<string> SaveDocument(
        [Description("The path where the document should be saved within the collection")] string documentPath,
        [Description("The content to save to the document")] string content,
        [Description("The name of the collection to save the document to. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var directoryPath = Path.GetDirectoryName(documentPath) ?? "";
            var fileName = Path.GetFileName(documentPath);

            if (string.IsNullOrEmpty(fileName))
                return $"Invalid document path: '{documentPath}'. Must include a filename.";

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await collection.SaveFileAsync(directoryPath, fileName, stream);

            return $"Document '{documentPath}' successfully saved to collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error saving document '{documentPath}' to collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Deletes a file from a specified collection. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> DeleteFile(
        [Description("The path to the file to delete within the collection. Can be inferred from context URL 'Content/{collection}/{path}'.")] string? filePath = null,
        [Description("The name of the collection containing the file. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = filePath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedFilePath))
            return "No file path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await collection.DeleteFileAsync(resolvedFilePath);
            return $"File '{resolvedFilePath}' successfully deleted from collection '{resolvedCollectionName}'.";
        }
        catch (FileNotFoundException)
        {
            return $"File '{resolvedFilePath}' not found in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error deleting file '{resolvedFilePath}' from collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Creates a new folder in a specified collection. When used with context area 'Content' and Id '{collection}/{path}', the collection can be automatically parsed from the context.")]
    public async Task<string> CreateFolder(
        [Description("The path of the folder to create within the collection")] string folderPath,
        [Description("The name of the collection to create the folder in. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await collection.CreateFolderAsync(folderPath);
            return $"Folder '{folderPath}' successfully created in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error creating folder '{folderPath}' in collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Deletes a folder from a specified collection. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> DeleteFolder(
        [Description("The path to the folder to delete within the collection. Can be inferred from context URL 'Content/{collection}/{path}'.")] string? folderPath = null,
        [Description("The name of the collection containing the folder. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFolderPath = folderPath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedFolderPath))
            return "No folder path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await collection.DeleteFolderAsync(resolvedFolderPath);
            return $"Folder '{resolvedFolderPath}' successfully deleted from collection '{resolvedCollectionName}'.";
        }
        catch (DirectoryNotFoundException)
        {
            return $"Folder '{resolvedFolderPath}' not found in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error deleting folder '{resolvedFolderPath}' from collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets the article catalog for a collection with filtering options. When used with context area 'Content' and Id '{collection}/{path}', the collection can be automatically parsed from the context.")]
    public async Task<string> GetArticleCatalog(
        [Description("Optional: Maximum number of articles to return")] int? maxResults = null,
        [Description("The name of the collection to get articles from. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        try
        {
            var options = new ArticleCatalogOptions
            {
                Collections = [resolvedCollectionName],
                PageSize = maxResults ?? 10
            };

            var articles = await contentService.GetArticleCatalogAsync(options, cancellationToken);
            if (!articles.Any())
                return $"No articles found in collection '{resolvedCollectionName}'.";

            return string.Join("\n\n", articles.Select(a =>
                $"Title: {a.Title}\n" +
                $"Path: {a.Path}\n" +
                $"Abstract: {a.Abstract ?? "No summary"}\n" +
                $"Published: {a.Published:yyyy-MM-dd}\n" +
                $"Last Updated: {a.LastUpdated:yyyy-MM-dd}"));
        }
        catch (Exception ex)
        {
            return $"Error getting article catalog for collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets a specific article with its metadata and content from a collection. When used with context area 'Content' and Id '{collection}/{path}', the collection and article ID are automatically parsed from the context.")]
    public async Task<string> GetArticle(
        [Description("The article identifier (path without .md extension). Can be inferred from context URL 'Content/{collection}/{path}'.")] string? articleId = null,
        [Description("The name of the collection containing the article. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedArticleId = articleId ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedArticleId))
            return "No article ID specified and no path found in context.";

        try
        {
            var articleObservable = await contentService.GetArticleAsync(resolvedCollectionName, resolvedArticleId, cancellationToken);

            // Get the current value from the observable
            var article = await articleObservable.FirstOrDefaultAsync();

            if (article == null)
                return $"Article '{resolvedArticleId}' not found in collection '{resolvedCollectionName}'.";

            // Try to get the markdown content as well
            var documentPath = resolvedArticleId.EndsWith(".md") ? resolvedArticleId : $"{resolvedArticleId}.md";
            var contentStream = await contentService.GetContentAsync(resolvedCollectionName, documentPath, cancellationToken);

            string content = "Content not available";
            if (contentStream != null)
            {
                await using (contentStream)
                {
                    using var reader = new StreamReader(contentStream);
                    content = await reader.ReadToEndAsync(cancellationToken);
                }
            }

            return $"Article: {article}\n\n--- Content ---\n{content}";
        }
        catch (Exception ex)
        {
            return $"Error getting article '{resolvedArticleId}' from collection: {ex.Message}";
        }
    }

    [KernelFunction]
    [Description("Gets the MIME content type for a file based on its extension. When used with context area 'Content' and Id '{collection}/{path}', the collection and path are automatically parsed from the context.")]
    public async Task<string> GetContentType(
        [Description("The path to the file. Can be inferred from context URL 'Content/{collection}/{path}'.")] string? filePath = null,
        [Description("The name of the collection. If not provided, parsed from context URL 'Content/{collection}/{path}'.")] string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCollectionName = GetCollectionName(collectionName);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = filePath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedFilePath))
            return "No file path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var contentType = collection.GetContentType(resolvedFilePath);
            return $"Content type for '{resolvedFilePath}': {contentType}";
        }
        catch (Exception ex)
        {
            return $"Error getting content type for '{resolvedFilePath}': {ex.Message}";
        }
    }

    public KernelPlugin CreateKernelPlugin()
    {
        var plugin = KernelPluginFactory.CreateFromFunctions(
            nameof(ContentCollectionPlugin),
            GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<KernelFunctionAttribute>() != null)
                .Select(m => KernelFunctionFactory.CreateFromMethod(m, this))
        );
        return plugin;
    }
}
