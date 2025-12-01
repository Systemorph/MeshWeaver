using System.ComponentModel;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Generalized plugin for reading and writing files to configured collections.
/// Supports context resolution via LayoutAreaReference and dynamic collection configuration.
///
/// Context Resolution:
/// - When LayoutAreaReference.Area is "Content" or "Collection"
/// - LayoutAreaReference.Id format: "{collection}/{path}"
/// - The plugin automatically parses collection and path from the Id
///
/// Examples:
/// 1. Viewing "Slip.md" in "Submissions@Microsoft@2026" collection:
///    - Area: "Content" or "Collection"
///    - Id: "Submissions@Microsoft@2026/Slip.md"
///    - Parsed collection: "Submissions@Microsoft@2026"
///    - Parsed path: "Slip.md"
///
/// 2. Viewing root folder of "Documents" collection:
///    - Area: "Collection"
///    - Id: "Documents/"
///    - Parsed collection: "Documents"
///    - Parsed path: "/" (root)
/// </summary>
public class ContentPlugin
{
    private readonly IMessageHub hub;
    private readonly IContentService contentService;
    private readonly ContentPluginConfig config;
    private readonly IAgentChat? chat;
    private readonly ILogger<ContentPlugin> logger;

    /// <summary>
    /// Creates a ContentPlugin with basic functionality (no context resolution).
    /// </summary>
    public ContentPlugin(IMessageHub hub)
        : this(hub, new ContentPluginConfig { Collections = [] }, null!)
    {
    }

    /// <summary>
    /// Creates a ContentPlugin with context resolution and dynamic collection configuration.
    /// </summary>
    public ContentPlugin(IMessageHub hub, ContentPluginConfig config, IAgentChat chat)
    {
        this.hub = hub;
        this.config = config;
        this.chat = chat;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<ContentPlugin>>();
        contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        // Add all configured collections to IContentService
        foreach (var collectionConfig in config.Collections)
        {
            contentService.AddConfiguration(collectionConfig);
        }
    }

    /// <summary>
    /// Parses a path that may contain the collection:path syntax (e.g., "Submissions@Microsoft@2026:folder/file.xlsx").
    /// Returns the collection name and the file path separately.
    /// The path portion does not include a leading slash.
    /// </summary>
    private (string? Collection, string? Path) ParseCollectionPath(string? combinedPath)
    {
        if (string.IsNullOrEmpty(combinedPath))
            return (null, null);

        // Check for collection:path syntax (e.g., "Submissions@Microsoft@2026:folder/file.xlsx")
        // The collection name typically contains @ separators but not colons, so split on first ":"
        var colonIndex = combinedPath.IndexOf(':');
        if (colonIndex > 0 && colonIndex < combinedPath.Length - 1)
        {
            var collection = combinedPath[..colonIndex];
            var path = combinedPath[(colonIndex + 1)..];
            // Ensure path starts with / for consistency
            if (!path.StartsWith('/'))
                path = "/" + path;
            return (collection, path);
        }

        return (null, combinedPath);
    }

    /// <summary>
    /// Gets the collection name from the parsed path or resolves from context.
    /// The collection is resolved in this order:
    /// 1. Parsed collection from 'collection:path' syntax if provided
    /// 2. Parsed from LayoutAreaReference.Id (format: "{collection}/{path}") ONLY when Area is "Content" or "Collection"
    /// 3. Via ContextToConfigMap if configured
    /// 4. Falls back to first configured collection
    /// </summary>
    private string? GetCollectionName(string? parsedCollection)
    {
        if (!string.IsNullOrEmpty(parsedCollection))
            return parsedCollection;

        if (chat == null)
            return config.Collections.FirstOrDefault()?.Name;

        // Only parse from LayoutAreaReference.Id when area is "Content" or "Collection"
        if (chat.Context?.LayoutArea != null)
        {
            var area = chat.Context.LayoutArea.Area;
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
            // Add the dynamically created config to IContentService
            contentService.AddConfiguration(contextConfig);
            return contextConfig.Name;
        }

        // Fall back to the first collection from config as default
        return config.Collections.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Gets the path from the agent's LayoutAreaReference.Id.
    /// When LayoutAreaReference.Area is "Content" or "Collection", the Id format is "{collection}/{path}".
    /// This method extracts and returns the path portion.
    /// For example, "Submissions@Microsoft@2026/Slip.md" returns "Slip.md".
    /// Returns null if no context is available.
    /// </summary>
    private string? GetPathFromContext()
    {
        if (chat?.Context?.LayoutArea == null)
            return null;

        var area = chat.Context.LayoutArea.Area;
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

    [Description("Gets the content of a file. Supports Excel, Word, PDF, and any other format. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")]
    public async Task<string> GetContent(
        [Description("The path to the file using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")]
        string? filePath = null,
        [Description("Optional: number of rows to read. If null, reads entire file. For Excel files, reads first N rows from each worksheet.")]
        int? numberOfRows = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("GetContent called with filePath={FilePath}, numberOfRows={NumberOfRows}",
            filePath, numberOfRows);

        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(filePath);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = parsedPath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedFilePath))
            return "No file path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await using var stream = await collection.GetContentAsync(resolvedFilePath, cancellationToken);
            if (stream == null)
                return $"File '{resolvedFilePath}' not found in collection '{resolvedCollectionName}'.";

            // Check file type and read accordingly
            var extension = Path.GetExtension(resolvedFilePath).ToLowerInvariant();
            if (extension == ".xlsx" || extension == ".xls")
            {
                return ReadExcelFile(stream, resolvedFilePath, numberOfRows);
            }
            else if (extension == ".docx")
            {
                return ReadWordFile(stream, resolvedFilePath, numberOfRows);
            }
            else if (extension == ".pdf")
            {
                return ReadPdfFile(stream, resolvedFilePath, numberOfRows);
            }

            // For other files, read as text
            using var reader = new StreamReader(stream);
            if (numberOfRows.HasValue)
            {
                var sb = new StringBuilder();
                var linesRead = 0;
                while (linesRead < numberOfRows.Value)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                        break;
                    sb.AppendLine(line);
                    linesRead++;
                }
                return sb.ToString();
            }
            else
            {
                var content = await reader.ReadToEndAsync(cancellationToken);
                return content;
            }
        }
        catch (FileNotFoundException)
        {
            return $"File '{resolvedFilePath}' not found in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error reading file '{resolvedFilePath}' from collection '{resolvedCollectionName}': {ex.Message}";
        }
    }

    private string ReadExcelFile(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var wb = new XLWorkbook(stream);
            var sb = new StringBuilder();

            foreach (var ws in wb.Worksheets)
            {
                var used = ws.RangeUsed();
                sb.AppendLine($"## Sheet: {ws.Name}");
                sb.AppendLine();
                if (used is null)
                {
                    sb.AppendLine("(No data)");
                    sb.AppendLine();
                    continue;
                }

                var firstRow = used.FirstRow().RowNumber();
                var lastRow = numberOfRows.HasValue
                    ? Math.Min(used.FirstRow().RowNumber() + numberOfRows.Value - 1, used.LastRow().RowNumber())
                    : used.LastRow().RowNumber();
                var firstCol = 1;
                var lastCol = used.LastColumn().ColumnNumber();

                // Build markdown table with column letters as headers
                var columnHeaders = new List<string> { "Row" };
                for (var c = firstCol; c <= lastCol; c++)
                {
                    // Convert column number to Excel letter (1=A, 2=B, ..., 27=AA, etc.)
                    columnHeaders.Add(GetExcelColumnLetter(c));
                }

                // Header row
                sb.AppendLine("| " + string.Join(" | ", columnHeaders) + " |");
                // Separator row
                sb.AppendLine("|" + string.Join("", columnHeaders.Select(_ => "---:|")));

                // Data rows
                for (var r = firstRow; r <= lastRow; r++)
                {
                    var rowVals = new List<string> { r.ToString() };
                    for (var c = firstCol; c <= lastCol; c++)
                    {
                        var cell = ws.Cell(r, c);
                        var raw = cell.GetValue<string>();
                        var val = raw?.Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
                        // Empty cells show as empty in table
                        rowVals.Add(string.IsNullOrEmpty(val) ? "" : val);
                    }

                    sb.AppendLine("| " + string.Join(" | ", rowVals) + " |");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading Excel file '{filePath}': {ex.Message}";
        }
    }

    private static string GetExcelColumnLetter(int columnNumber)
    {
        var columnLetter = "";
        while (columnNumber > 0)
        {
            var modulo = (columnNumber - 1) % 26;
            columnLetter = Convert.ToChar('A' + modulo) + columnLetter;
            columnNumber = (columnNumber - 1) / 26;
        }
        return columnLetter;
    }

    private string ReadWordFile(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var wordDoc = WordprocessingDocument.Open(stream, false);
            var body = wordDoc.MainDocumentPart?.Document.Body;

            if (body == null)
                return $"Word document '{filePath}' has no readable content.";

            var sb = new StringBuilder();
            sb.AppendLine($"# Document: {Path.GetFileName(filePath)}");
            sb.AppendLine();

            var paragraphs = body.Elements<Paragraph>().ToList();
            var paragraphsToRead = numberOfRows.HasValue
                ? paragraphs.Take(numberOfRows.Value).ToList()
                : paragraphs;

            foreach (var paragraph in paragraphsToRead)
            {
                var text = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
            }

            // Also handle tables
            var tables = body.Elements<Table>().ToList();
            foreach (var table in tables)
            {
                sb.AppendLine("## Table");
                sb.AppendLine();

                var rows = table.Elements<TableRow>().ToList();
                var rowsToRead = numberOfRows.HasValue
                    ? rows.Take(numberOfRows.Value).ToList()
                    : rows;

                foreach (var row in rowsToRead)
                {
                    var cells = row.Elements<TableCell>().ToList();
                    var cellTexts = cells.Select(c => c.InnerText.Replace('|', '\\').Trim()).ToList();
                    sb.AppendLine("| " + string.Join(" | ", cellTexts) + " |");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading Word document '{filePath}': {ex.Message}";
        }
    }

    private string ReadPdfFile(Stream stream, string filePath, int? numberOfRows)
    {
        try
        {
            using var pdfDocument = PdfDocument.Open(stream);
            var sb = new StringBuilder();
            sb.AppendLine($"# PDF Document: {Path.GetFileName(filePath)}");
            sb.AppendLine($"Total pages: {pdfDocument.NumberOfPages}");
            sb.AppendLine();

            var pagesToRead = numberOfRows.HasValue
                ? Math.Min(numberOfRows.Value, pdfDocument.NumberOfPages)
                : pdfDocument.NumberOfPages;

            for (int pageNum = 1; pageNum <= pagesToRead; pageNum++)
            {
                var page = pdfDocument.GetPage(pageNum);
                sb.AppendLine($"## Page {pageNum}");
                sb.AppendLine();

                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
                else
                {
                    sb.AppendLine("(No text content)");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading PDF document '{filePath}': {ex.Message}";
        }
    }

    [Description("Saves content as a file. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder/file.xlsx').")]
    public async Task<string> SaveFile(
        [Description("The path where the file should be saved using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.txt').")] string filePath,
        [Description("The content to save to the file")] string content,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("SaveFile called with filePath={FilePath}, contentLength={ContentLength}",
            filePath, content?.Length ?? 0);
        if (content is null)
            return "No content supplied";

        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(filePath);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = parsedPath;
        if (string.IsNullOrEmpty(resolvedFilePath))
            return "File path is required.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            // Ensure directory structure exists if the collection has a base path
            EnsureDirectoryExists(collection, resolvedFilePath);

            // Extract directory and filename components
            var directoryPath = Path.GetDirectoryName(resolvedFilePath) ?? "";
            var fileName = Path.GetFileName(resolvedFilePath);

            if (string.IsNullOrEmpty(fileName))
                return $"Invalid file path: '{resolvedFilePath}'. Must include a filename.";

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await collection.SaveFileAsync(directoryPath, fileName, stream);

            return $"File '{resolvedFilePath}' successfully saved to collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error saving file '{resolvedFilePath}' to collection '{resolvedCollectionName}': {ex.Message}";
        }
    }

    [Description("Lists all files at a given path. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder' or 'Submissions@Microsoft@2026:' for root).")]
    public async Task<string> ListFiles(
        [Description("Directory path using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder'). Use 'collection:' for root.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("ListFiles called with path={Path}", path);

        try
        {
            // Parse collection:path syntax
            var (parsedCollection, parsedPath) = ParseCollectionPath(path);
            var resolvedCollectionName = GetCollectionName(parsedCollection);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = parsedPath ?? GetPathFromContext() ?? "/";
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

    [Description("Lists all folders at a given path. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder' or 'Submissions@Microsoft@2026:' for root).")]
    public async Task<string> ListFolders(
        [Description("Directory path using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder'). Use 'collection:' for root.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse collection:path syntax
            var (parsedCollection, parsedPath) = ParseCollectionPath(path);
            var resolvedCollectionName = GetCollectionName(parsedCollection);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = parsedPath ?? GetPathFromContext() ?? "/";
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

    [Description("Lists all files and folders at a given path. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder' or 'Submissions@Microsoft@2026:' for root).")]
    public async Task<string> ListCollectionItems(
        [Description("Directory path using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder'). Use 'collection:' for root.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse collection:path syntax
            var (parsedCollection, parsedPath) = ParseCollectionPath(path);
            var resolvedCollectionName = GetCollectionName(parsedCollection);
            if (string.IsNullOrEmpty(resolvedCollectionName))
                return "No collection specified and no default collection configured.";

            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            var resolvedPath = parsedPath ?? GetPathFromContext() ?? "/";
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


    [Description("Deletes a file or folder. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder/file.xlsx' or 'Submissions@Microsoft@2026:folder').")]
    public async Task<string> Delete(
        [Description("Path to delete using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx' for files or 'Submissions@Microsoft@2026:folder' for folders).")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(path);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedPath = parsedPath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedPath))
            return "No path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            // Determine if path is likely a file (has extension) or folder
            var hasExtension = Path.HasExtension(resolvedPath);

            if (hasExtension)
            {
                // Try file first, then folder
                try
                {
                    await collection.DeleteFileAsync(resolvedPath);
                    return $"File '{resolvedPath}' successfully deleted from collection '{resolvedCollectionName}'.";
                }
                catch (FileNotFoundException)
                {
                    // Try as folder
                    try
                    {
                        await collection.DeleteFolderAsync(resolvedPath);
                        return $"Folder '{resolvedPath}' successfully deleted from collection '{resolvedCollectionName}'.";
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return $"'{resolvedPath}' not found in collection '{resolvedCollectionName}'.";
                    }
                }
            }
            else
            {
                // Try folder first, then file
                try
                {
                    await collection.DeleteFolderAsync(resolvedPath);
                    return $"Folder '{resolvedPath}' successfully deleted from collection '{resolvedCollectionName}'.";
                }
                catch (DirectoryNotFoundException)
                {
                    // Try as file
                    try
                    {
                        await collection.DeleteFileAsync(resolvedPath);
                        return $"File '{resolvedPath}' successfully deleted from collection '{resolvedCollectionName}'.";
                    }
                    catch (FileNotFoundException)
                    {
                        return $"'{resolvedPath}' not found in collection '{resolvedCollectionName}'.";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return $"Error deleting '{resolvedPath}' from collection '{resolvedCollectionName}': {ex.Message}";
        }
    }

    [Description("Creates a new folder. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder/subfolder').")]
    public async Task<string> CreateFolder(
        [Description("The folder path to create using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:newfolder').")] string folderPath,
        CancellationToken cancellationToken = default)
    {
        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(folderPath);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFolderPath = parsedPath ?? folderPath;
        if (string.IsNullOrEmpty(resolvedFolderPath))
            return "Folder path is required.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await collection.CreateFolderAsync(resolvedFolderPath);
            return $"Folder '{resolvedFolderPath}' successfully created in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error creating folder '{resolvedFolderPath}' in collection: {ex.Message}";
        }
    }

    //[Description("Gets the article catalog for a collection with filtering options. If collection not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    //public async Task<string> GetArticleCatalog(
    //    [Description("Optional: Maximum number of articles to return")] int? maxResults = null,
    //    [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
    //    CancellationToken cancellationToken = default)
    //{
    //    var resolvedCollectionName = GetCollectionName(collectionName);
    //    if (string.IsNullOrEmpty(resolvedCollectionName))
    //        return "No collection specified and no default collection configured.";

    //    try
    //    {
    //        var options = new ArticleCatalogOptions
    //        {
    //            Collections = [resolvedCollectionName],
    //            PageSize = maxResults ?? 10
    //        };

    //        var articles = await contentService.GetArticleCatalogAsync(options, cancellationToken);
    //        if (!articles.Any())
    //            return $"No articles found in collection '{resolvedCollectionName}'.";

    //        return string.Join("\n\n", articles.Select(a =>
    //            $"Title: {a.Title}\n" +
    //            $"Path: {a.Path}\n" +
    //            $"Abstract: {a.Abstract ?? "No summary"}\n" +
    //            $"Published: {a.Published:yyyy-MM-dd}\n" +
    //            $"Last Updated: {a.LastUpdated:yyyy-MM-dd}"));
    //    }
    //    catch (Exception ex)
    //    {
    //        return $"Error getting article catalog for collection: {ex.Message}";
    //    }
    //}

    //[Description("Gets a specific article with its metadata and content from a collection. If collection/articleId not provided: when Area='Content' or 'Collection', parses from LayoutAreaReference.Id ('{collection}/{path}'); otherwise uses ContextToConfigMap or plugin config.")]
    //public async Task<string> GetArticle(
    //    [Description("Article identifier (path without .md). If omitted: when Area='Content'/'Collection', extracts from Id (after first '/'); else null.")] string? articleId = null,
    //    [Description("Collection name. If omitted: when Area='Content'/'Collection', extracts from Id (before '/'); else uses ContextToConfigMap/config.")] string? collectionName = null,
    //    CancellationToken cancellationToken = default)
    //{
    //    var resolvedCollectionName = GetCollectionName(collectionName);
    //    if (string.IsNullOrEmpty(resolvedCollectionName))
    //        return "No collection specified and no default collection configured.";

    //    var resolvedArticleId = articleId ?? GetPathFromContext();
    //    if (string.IsNullOrEmpty(resolvedArticleId))
    //        return "No article ID specified and no path found in context.";

    //    try
    //    {
    //        var articleObservable = await contentService.GetArticleAsync(resolvedCollectionName, resolvedArticleId, cancellationToken);

    //        // Get the current value from the observable
    //        var article = await articleObservable.FirstOrDefaultAsync();

    //        if (article == null)
    //            return $"Article '{resolvedArticleId}' not found in collection '{resolvedCollectionName}'.";

    //        // Try to get the markdown content as well
    //        var documentPath = resolvedArticleId.EndsWith(".md") ? resolvedArticleId : $"{resolvedArticleId}.md";
    //        var contentStream = await contentService.GetContentAsync(resolvedCollectionName, documentPath, cancellationToken);

    //        string content = "Content not available";
    //        if (contentStream != null)
    //        {
    //            await using (contentStream)
    //            {
    //                using var reader = new StreamReader(contentStream);
    //                content = await reader.ReadToEndAsync(cancellationToken);
    //            }
    //        }

    //        return $"Article: {article}\n\n--- Content ---\n{content}";
    //    }
    //    catch (Exception ex)
    //    {
    //        return $"Error getting article '{resolvedArticleId}' from collection: {ex.Message}";
    //    }
    //}

    [Description("Gets the MIME content type for a file based on its extension. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder/file.xlsx').")]
    public async Task<string> GetContentType(
        [Description("File path using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")] string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(filePath);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = parsedPath ?? GetPathFromContext();
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

    [Description("Checks if a specific file exists. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:folder/file.xlsx').")]
    public async Task<string> FileExists(
        [Description("File path using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")] string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(filePath);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedFilePath = parsedPath ?? GetPathFromContext();
        if (string.IsNullOrEmpty(resolvedFilePath))
            return "No file path specified and no path found in context.";

        try
        {
            var collection = await contentService.GetCollectionAsync(resolvedCollectionName, cancellationToken);
            if (collection == null)
                return $"Collection '{resolvedCollectionName}' not found.";

            await using var stream = await collection.GetContentAsync(resolvedFilePath, cancellationToken);
            if (stream == null)
                return $"File '{resolvedFilePath}' does not exist in collection '{resolvedCollectionName}'.";

            return $"File '{resolvedFilePath}' exists in collection '{resolvedCollectionName}'.";
        }
        catch (FileNotFoundException)
        {
            return $"File '{resolvedFilePath}' does not exist in collection '{resolvedCollectionName}'.";
        }
        catch (Exception ex)
        {
            return $"Error checking file '{resolvedFilePath}' in collection '{resolvedCollectionName}': {ex.Message}";
        }
    }

    [Description("Generates a unique filename with timestamp for saving temporary files.")]
    public string GenerateUniqueFileName(
        [Description("The base name for the file (without extension)")] string baseName,
        [Description("The file extension (e.g., 'json', 'txt')")] string extension)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        return $"{baseName}_{timestamp}.{extension.TrimStart('.')}";
    }

    [Description("Imports data from a file to a specified address. " +
                 "Use the 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")]
    public async Task<string> Import(
        [Description("The path to the file to import using 'collection:path' syntax (e.g., 'Submissions@Microsoft@2026:file.xlsx').")] string path,
        [Description("The target address for the import (optional if default address is configured). Format: '{AddressType}/{AddressId}'.")] string? address = null,
        [Description("The import format to use (optional, defaults to 'Default')")] string? format = null,
        [Description("Optional import configuration as JSON string. When provided, this will be used instead of the format parameter.")] string? configuration = null,
        [Description("Whether to override any other instances as snapshot import. Default is false.")] bool snapshot = false)
    {
        logger.LogInformation("Import called with path={Path}, address={Address}, format={Format}",
            path, address, format);

        // Parse collection:path syntax
        var (parsedCollection, parsedPath) = ParseCollectionPath(path);
        var resolvedCollectionName = GetCollectionName(parsedCollection);
        if (string.IsNullOrEmpty(resolvedCollectionName))
            return "No collection specified and no default collection configured.";

        var resolvedPath = parsedPath ?? path;

        if (address == null)
            return "Target address is required.";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // Parse the address - handle both string and Address types
            var targetAddress = hub.GetAddress(address);

            // Build ImportRequest JSON structure
            var importRequestJson = new JsonObject
            {
                ["$type"] = "MeshWeaver.Import.ImportRequest",
                ["source"] = new JsonObject
                {
                    ["$type"] = "MeshWeaver.Import.CollectionSource",
                    ["collection"] = resolvedCollectionName,
                    ["path"] = resolvedPath
                },
                ["format"] = format ?? "Default"
            };

            // Add configuration if provided
            JsonNode? configNode = null;
            if (!string.IsNullOrWhiteSpace(configuration))
            {
                configNode = JsonNode.Parse(configuration);
                if (configNode != null)
                {
                    importRequestJson["configuration"] = configNode;
                }
            }

            if (snapshot)
            {
                importRequestJson["snapshot"] = JsonNode.Parse("{\"snapshot\": true}");
            }

            // Serialize and deserialize through hub's serializer to get proper type
            var jsonString = importRequestJson.ToJsonString();
            var importRequestObj = JsonSerializer.Deserialize<object>(jsonString, hub.JsonSerializerOptions)!;

            // Post the request to the hub
            var responseMessage = await hub.AwaitResponse(
                importRequestObj,
                o => o.WithTarget(targetAddress),
                cts.Token
            );

            // Auto-save configuration if it was provided and the import succeeded
            if (configNode != null && responseMessage != null)
            {
                try
                {
                    // Check if this is an ExcelImportConfiguration with a name field
                    var configObj = configNode as JsonObject;
                    var configTypeName = configObj?["$type"]?.ToString();
                    var configName = configObj?["name"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(configTypeName) &&
                        configTypeName.Contains("ExcelImportConfiguration") &&
                        !string.IsNullOrWhiteSpace(configName))
                    {
                        // Clone the configuration node to avoid "node already has a parent" error
                        var configClone = JsonNode.Parse(configNode.ToJsonString());

                        // Save the configuration using DataPlugin
                        var updateRequest = new JsonObject
                        {
                            ["$type"] = "MeshWeaver.Data.UpdateDataRequest",
                            ["type"] = "ExcelImportConfiguration",
                            ["data"] = configClone
                        };

                        var updateJsonString = updateRequest.ToJsonString();
                        var updateRequestObj = JsonSerializer.Deserialize<object>(updateJsonString, hub.JsonSerializerOptions)!;

                        await hub.AwaitResponse(
                            updateRequestObj,
                            o => o.WithTarget(targetAddress),
                            cts.Token
                        );

                        logger.LogInformation("Auto-saved ExcelImportConfiguration: {ConfigName}", configName);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Configuration save was cancelled, but import succeeded
                    logger.LogWarning("Configuration save was cancelled, but import succeeded");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to auto-save import configuration, but import succeeded");
                    // Don't fail the import if configuration save fails
                }
            }

            // Serialize the response back to JSON for processing
            var responseJson = JsonSerializer.Serialize(responseMessage, hub.JsonSerializerOptions);
            var responseObj = JsonNode.Parse(responseJson)!;

            var log = responseObj["log"] as JsonObject;
            var status = log?["status"]?.ToString() ?? "Unknown";
            var messages = log?["messages"] as JsonArray ?? new JsonArray();

            var result = $"Import {status.ToLower()}.\n";
            if (messages.Count > 0)
            {
                result += "Log messages:\n";
                foreach (var msg in messages)
                {
                    if (msg is JsonObject msgObj)
                    {
                        var level = msgObj["logLevel"]?.ToString() ?? "Info";
                        var message = msgObj["message"]?.ToString() ?? "";
                        result += $"  [{level}] {message}\n";
                    }
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return "Operation was cancelled and could not be completed.";
        }
        catch (Exception ex)
        {
            return $"Error importing file '{resolvedPath}' from collection '{resolvedCollectionName}' to address '{address}': {ex.Message}";
        }
    }

    /// <summary>
    /// Creates AITools from this instance.
    /// </summary>
    public IList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(GetContent),
            AIFunctionFactory.Create(SaveFile),
            AIFunctionFactory.Create(ListFiles),
            AIFunctionFactory.Create(ListFolders),
            AIFunctionFactory.Create(ListCollectionItems),
            AIFunctionFactory.Create(Delete),
            AIFunctionFactory.Create(CreateFolder),
            AIFunctionFactory.Create(GetContentType),
            AIFunctionFactory.Create(FileExists),
            AIFunctionFactory.Create(GenerateUniqueFileName),
            AIFunctionFactory.Create(Import)
        ];
    }

    /// <summary>
    /// Ensures that the directory structure exists for the given file path within the collection.
    /// </summary>
    /// <param name="collection">The collection to check</param>
    /// <param name="filePath">The file path that may contain directories</param>
    private void EnsureDirectoryExists(object collection, string filePath)
    {
        try
        {
            // Normalize path separators and get the directory path from the file path
            var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
            var directoryPath = Path.GetDirectoryName(normalizedPath);

            if (string.IsNullOrEmpty(directoryPath) || directoryPath == "." || directoryPath == Path.DirectorySeparatorChar.ToString())
            {
                // No directory structure needed, file is in root
                return;
            }

            // Try to get the collection's base path using reflection if available
            var collectionType = collection.GetType();
            var basePathProperty = collectionType.GetProperty("BasePath") ??
                                 collectionType.GetProperty("Path") ??
                                 collectionType.GetProperty("RootPath");

            if (basePathProperty != null)
            {
                var basePath = basePathProperty.GetValue(collection) as string;
                if (!string.IsNullOrEmpty(basePath))
                {
                    var fullDirectoryPath = Path.Combine(basePath, directoryPath);
                    Directory.CreateDirectory(fullDirectoryPath);
                }
            }
        }
        catch (Exception)
        {
            // If we can't create directories through reflection,
            // let the SaveFileAsync method handle any directory creation or fail gracefully
        }
    }
}
