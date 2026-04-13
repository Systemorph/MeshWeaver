using System.Runtime.CompilerServices;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections.Completion;

/// <summary>
/// Provides autocomplete items for content collections.
/// Uses fzf-style fuzzy matching (FuzzyScorer) so a query matching ANY word in the filename
/// scores high — e.g., "two" matches "one two three.docx", "thr" matches "three" word.
/// Priority scale: word-boundary fuzzy matches score in the thousands; proximity boost +1000 for local content.
/// </summary>
public class ContentAutocompleteProvider(IContentService contentService) : IAutocompleteProvider
{
    private static readonly FuzzyScorer Scorer = new();

    /// <inheritdoc />
    public string? Prefix => "content";

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
                // For the default "content" collection, omit the collection name to avoid content/content/file
                var contentPath = collection.Collection == "content"
                    ? pathWithoutLeadingSlash
                    : $"{collection.Collection}/{pathWithoutLeadingSlash}";
                var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                var description = IsConvertibleDocument(ext)
                    ? $"{contentPath} (converts to markdown)"
                    : contentPath;

                // Build insert text: use relative content/ path when in context
                var insertText = FormatInsertText(contentPath, collection.Address, contextPath);

                // Proximity boost: content in same address as context gets a bonus
                var addressStr = collection.Address?.ToString();
                if (!string.IsNullOrEmpty(contextPath) &&
                    !string.IsNullOrEmpty(addressStr) &&
                    (contextPath.Equals(addressStr, StringComparison.OrdinalIgnoreCase) ||
                     contextPath.StartsWith(addressStr + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    score += 1000; // local content
                }

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
    /// Scores a file name against the search text using fzf-style fuzzy matching.
    /// Case-insensitive. Word-boundary matches (e.g., "thr" against "one two three.docx" → "three")
    /// score high due to BonusAfterSeparator. Returns 0 if no match.
    /// Scaled to thousands to compete with postgres ts_rank-based scores.
    /// </summary>
    private static int ScoreMatch(string fileName, string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            return 100; // Return all with low priority when no query

        // Drop extension for scoring so "report" matches "report.md" without penalty
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Use FuzzyScorer (case-insensitive, word-boundary aware)
        var scored = Scorer.Score(new[] { nameWithoutExt }, searchText, s => s).FirstOrDefault();
        if (scored == null || scored.Score <= 0)
            return 0;

        // FuzzyScorer typically returns scores in 10s-100s range. Scale to compete with
        // node scoring (which is in thousands). Multiply by 30 keeps strong matches well above 1000.
        return scored.Score * 30;
    }

    /// <summary>
    /// Formats the insert text for autocomplete.
    /// Uses relative content/ path when contextPath matches the collection address.
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
            reference = $"@content/{contentPath}";
        }
        else if (!string.IsNullOrEmpty(addressStr))
        {
            reference = $"@{addressStr}/content/{contentPath}";
        }
        else
        {
            reference = $"@content/{contentPath}";
        }

        // Wrap in quotes if path contains spaces
        if (reference.Contains(' '))
            reference = $"\"{reference}\"";

        return reference + " ";
    }

    /// <summary>
    /// Extracts the search text from the raw query.
    /// Strips @ prefix and any content: or content/ tag prefix, keeping just the filename portion.
    /// </summary>
    private static string ExtractSearchText(string query)
    {
        if (string.IsNullOrEmpty(query))
            return "";

        // Strip @ prefix
        var text = query.TrimStart('@');

        // Check for content: (legacy) or content/ (preferred) tag
        var contentColonIndex = text.IndexOf("content:", StringComparison.OrdinalIgnoreCase);
        var contentSlashIndex = text.IndexOf("content/", StringComparison.OrdinalIgnoreCase);

        int contentIndex;
        int skipLength;
        if (contentColonIndex >= 0)
        {
            contentIndex = contentColonIndex;
            skipLength = "content:".Length;
        }
        else if (contentSlashIndex >= 0)
        {
            contentIndex = contentSlashIndex;
            skipLength = "content/".Length;
        }
        else
        {
            contentIndex = -1;
            skipLength = 0;
        }

        if (contentIndex >= 0)
        {
            text = text[(contentIndex + skipLength)..];
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
