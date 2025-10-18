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
/// Context Resolution:
/// - When LayoutAreaReference.Area is "Content" or "Collection"
/// - LayoutAreaReference.Id format: "{collection}/{path}"
/// - The plugin automatically parses collection and path from the Id
///
/// Examples:
/// 1. Viewing "Slip.md" in "Submissions-Microsoft-2026" collection:
///    - Area: "Content" or "Collection"
///    - Id: "Submissions-Microsoft-2026/Slip.md"
///    - Parsed collection: "Submissions-Microsoft-2026"
///    - Parsed path: "Slip.md"
///
/// 2. Viewing root folder of "Documents" collection:
///    - Area: "Collection"
///    - Id: "Documents/"
///    - Parsed collection: "Documents"
///    - Parsed path: "/" (root)
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
    /// Gets the collection name from the provided parameter or parses from LayoutAreaReference.
    /// The collection is resolved in this order:
    /// 1. Explicit collectionName parameter if provided
    /// 2. Parsed from LayoutAreaReference.Id (format: "{collection}/{path}") ONLY when Area is "Content" or "Collection"
    /// 3. If area is NOT "Content" or "Collection", returns null to allow resolution via ContextToConfigMap or config
    /// </summary>
    private string? GetCollectionName(string? collectionName)
    {
        if (!string.IsNullOrEmpty(collectionName))
            return collectionName;

        // Only parse from LayoutAreaReference.Id when area is "Content" or "Collection"
        if (chat.Context?.LayoutArea != null)
        {
            var area = chat.Context.LayoutArea.Area?.ToString();
            if (area == "Content" || area == "Collection")
            {
                var id = chat.Context.LayoutArea.Id?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        return parts[0]; // First part is the collection name
                    }
                }
            }
        }

        // If not from Content/Collection area, try ContextToConfigMap
        if (config.ContextToConfigMap != null && chat.Context != null)
        {
            var contextConfig = config.ContextToConfigMap(chat.Context);
            if (contextConfig != null)
            {
                // Add the dynamically created config to IContentService
                contentService.AddConfiguration(contextConfig);
                return contextConfig.Name;
            }
        }

        // Fall back to the first collection from config as default
        return config.Collections.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Gets the path from the agent's LayoutAreaReference.Id.
    /// When LayoutAreaReference.Area is "Content" or "Collection", the Id format is "{collection}/{path}".
    /// This method extracts and returns the path portion.
    /// For example, "Submissions-Microsoft-2026/Slip.md" returns "Slip.md".
    /// Returns null if no context is available.
    /// </summary>
    private string? GetPathFromContext()
    {
        if (chat.Context?.LayoutArea == null)
            return null;

        var area = chat.Context.LayoutArea.Area?.ToString();
        if (area != "Content" && area != "Collection")
            return null;

        var id = chat.Context.LayoutArea.Id?.ToString();
        if (string.IsNullOrEmpty(id))
            return null;

        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
    [Description("Lists all files in a specified collection at a given path. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> ListFiles(
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
        [Description("Directory path (use '/' for root). If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? path = null,
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
    [Description("Lists all folders in a specified collection at a given path. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> ListFolders(
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
        [Description("Directory path (use '/' for root). If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? path = null,
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
    [Description("Lists all files and folders in a specified collection at a given path. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> ListCollectionItems(
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
        [Description("Directory path (use '/' for root). If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? path = null,
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
    [Description("Gets the content of a specific document from a collection. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> GetDocument(
        [Description("Document path in collection. If omitted: when Area='Content'/'Collection', extracts from Id (after first '/', e.g., 'Slip.md' from 'Submissions-Microsoft-2026/Slip.md'); else null.")] string? documentPath = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Saves content as a document to a specified collection. If collection not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> SaveDocument(
        [Description("The path where the document should be saved within the collection")] string documentPath,
        [Description("The content to save to the document")] string content,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Deletes a file from a specified collection. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> DeleteFile(
        [Description("File path to delete. If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? filePath = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Creates a new folder in a specified collection. If collection not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> CreateFolder(
        [Description("The path of the folder to create within the collection")] string folderPath,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Deletes a folder from a specified collection. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> DeleteFolder(
        [Description("Folder path to delete. If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? folderPath = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Gets the article catalog for a collection with filtering options. If collection not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> GetArticleCatalog(
        [Description("Optional: Maximum number of articles to return")] int? maxResults = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Gets a specific article with its metadata and content from a collection. If collection/articleId not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> GetArticle(
        [Description("Article identifier (path without .md). If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? articleId = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
    [Description("Gets the MIME content type for a file based on its extension. If collection/path not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    public async Task<string> GetContentType(
        [Description("File path. If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? filePath = null,
        [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
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
