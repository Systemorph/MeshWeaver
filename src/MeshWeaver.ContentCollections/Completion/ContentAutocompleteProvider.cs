using System.Runtime.CompilerServices;
using MeshWeaver.Data.Completion;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections.Completion;

/// <summary>
/// Provides autocomplete items for content collections.
/// Filters files by query relevance and scores them to compete fairly with postgres-backed results.
/// Priority scale: exact name match 3000, prefix match 2800, contains 2000.
/// </summary>
public class ContentAutocompleteProvider(IContentService contentService) : IAutocompleteProvider
{
    /// <inheritdoc />
    public async IAsyncEnumerable<AutocompleteItem> GetItemsAsync(
        string query,
        string? contextPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Strip @ prefix and any path before the query text
        var searchText = ExtractSearchText(query);

        // Iterate over all configured collections
        foreach (var config in contentService.GetAllCollectionConfigs())
        {
            var collection = await contentService.GetCollectionAsync(config.Name!, ct);
            if (collection == null)
                continue;

            await foreach (var item in EnumerateMatchingFilesAsync(collection, "/", searchText, contextPath))
            {
                yield return item;
            }
        }
    }

    private static async IAsyncEnumerable<AutocompleteItem> EnumerateMatchingFilesAsync(
        ContentCollection collection, string path, string searchText, string? contextPath)
    {
        IReadOnlyCollection<FileItem>? files = null;
        IReadOnlyCollection<FolderItem>? folders = null;
        try
        {
            files = await collection.GetFilesAsync(path);
            folders = await collection.GetFoldersAsync(path);
        }
        catch
        {
            // Skip paths that fail to enumerate
        }

        if (files != null)
        {
            foreach (var file in files)
            {
                var score = ScoreMatch(file.Name, searchText);
                if (score <= 0 && !string.IsNullOrEmpty(searchText))
                    continue; // Skip non-matching files when there's a query

                var pathWithoutLeadingSlash = file.Path.TrimStart('/');
                // For the default "content" collection, omit the collection name to avoid content:content/file
                var contentPath = collection.Collection == "content"
                    ? pathWithoutLeadingSlash
                    : $"{collection.Collection}/{pathWithoutLeadingSlash}";
                var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                var description = IsConvertibleDocument(ext)
                    ? $"{contentPath} (converts to markdown)"
                    : contentPath;

                // Build insert text: use relative content: path when in context
                var insertText = FormatInsertText(contentPath, collection.Address, contextPath);

                yield return new AutocompleteItem(
                    Label: file.Name,
                    InsertText: insertText,
                    Description: description,
                    Category: collection.DisplayName,
                    Priority: score,
                    Kind: AutocompleteKind.File,
                    Path: contentPath
                );
            }
        }

        if (folders != null)
        {
            foreach (var folder in folders)
            {
                await foreach (var item in EnumerateMatchingFilesAsync(collection, folder.Path, searchText, contextPath))
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>
    /// Scores a file name against the search text.
    /// Returns priority values that compete with postgres ts_rank-based scores.
    /// </summary>
    private static int ScoreMatch(string fileName, string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            return 100; // Return all with low priority when no query

        var nameLower = fileName.ToLowerInvariant();
        var queryLower = searchText.ToLowerInvariant();
        var nameWithoutExt = Path.GetFileNameWithoutExtension(nameLower);

        // Exact match (with or without extension)
        if (nameLower == queryLower || nameWithoutExt == queryLower)
            return 3000;

        // Starts with query
        if (nameLower.StartsWith(queryLower) || nameWithoutExt.StartsWith(queryLower))
            return 2800;

        // Contains query as substring
        if (nameLower.Contains(queryLower))
            return 2000;

        // Word-boundary match (query matches start of a word in the name)
        var words = nameWithoutExt.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(queryLower)))
            return 1500;

        return 0; // No match
    }

    /// <summary>
    /// Formats the insert text for autocomplete.
    /// Uses relative content: path when contextPath matches the collection address.
    /// Wraps in quotes if the path contains spaces.
    /// </summary>
    private static string FormatInsertText(string contentPath, Address? collectionAddress, string? contextPath)
    {
        string reference;

        // If the context is the same address as the collection, use relative path
        var addressStr = collectionAddress?.ToString();
        if (!string.IsNullOrEmpty(contextPath) &&
            !string.IsNullOrEmpty(addressStr) &&
            (contextPath.Equals(addressStr, StringComparison.OrdinalIgnoreCase) ||
             contextPath.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase)))
        {
            reference = $"@content:{contentPath}";
        }
        else if (!string.IsNullOrEmpty(addressStr))
        {
            reference = $"@{addressStr}/content:{contentPath}";
        }
        else
        {
            reference = $"@content:{contentPath}";
        }

        // Wrap in quotes if path contains spaces
        if (reference.Contains(' '))
            reference = $"\"{reference}\"";

        return reference + " ";
    }

    /// <summary>
    /// Extracts the search text from the raw query.
    /// Strips @ prefix and any content: tag prefix, keeping just the filename portion.
    /// </summary>
    private static string ExtractSearchText(string query)
    {
        if (string.IsNullOrEmpty(query))
            return "";

        // Strip @ prefix
        var text = query.TrimStart('@');

        // If it contains content: tag, extract the part after it
        var contentIndex = text.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
        if (contentIndex >= 0)
        {
            text = text[(contentIndex + "content:".Length)..];
            // Strip collection prefix if present (e.g., "collection/filename")
            var lastSlash = text.LastIndexOf('/');
            if (lastSlash >= 0)
                text = text[(lastSlash + 1)..];
        }
        else
        {
            // For plain queries like "@samp", just use the text directly
            // Strip any path prefix, keep last segment
            var lastSlash = text.LastIndexOf('/');
            if (lastSlash >= 0)
                text = text[(lastSlash + 1)..];
        }

        return text.Trim();
    }

    private static bool IsConvertibleDocument(string extension) => extension is ".docx";
}
