using System.Reactive.Linq;
using MeshWeaver.AI.Completion;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections.Completion;

/// <summary>
/// Provides autocomplete items for content collections.
/// Uses fzf-style fuzzy matching (FuzzyScorer) so a query matching ANY word in the filename
/// scores high — e.g., "two" matches "one two three.docx", "thr" matches "three" word.
/// Priority scale: word-boundary fuzzy matches score in the thousands; proximity boost +1000 for local content.
/// <para>Fully reactive — the collection load and the recursive file walk compose via
/// <c>SelectMany</c>/<c>Merge</c>; the genuine I/O leaves (<c>GetCollectionAsync</c>,
/// <c>GetFiles</c>/<c>GetFolders</c>) bridge through the <see cref="IIoPool"/> (no bare
/// <c>Observable.FromAsync</c>, no provider-level async-enumerable). Dedup by insert text
/// across collections is <c>Observable.Distinct</c>.</para>
/// </summary>
public class ContentAutocompleteProvider(
    IContentService contentService,
    IoPoolRegistry? ioPoolRegistry = null) : IAutocompleteProvider
{
    private static readonly FuzzyScorer Scorer = new();
    private readonly IIoPool _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;

    /// <inheritdoc />
    public string? Prefix => "content";

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null)
    {
        var searchText = ExtractSearchText(query);
        var items = contentService.GetAllCollectionConfigs().ToObservable()
            .SelectMany(config => _ioPool.Run(ct => contentService.GetCollectionAsync(config.Name!, ct)))
            .Where(collection => collection is not null)
            .SelectMany(collection => WalkCollection(collection!, "/", searchText, contextPath))
            // Same file can surface via multiple collections (parent/child hierarchy,
            // mapped collections pointing at the same storage path) — dedupe by insert text.
            .Distinct(item => item.InsertText);
        return AutocompleteSnapshots.FromItems(items, 50);
    }

    private IObservable<AutocompleteItem> WalkCollection(
        ContentCollection collection, string path, string searchText, string? contextPath)
    {
        // Files at this level + a recursive walk of every folder, merged. Each leaf
        // enumeration is bridged through the pool and Catch-guarded so a path that
        // fails to enumerate is skipped rather than tearing down the whole stream.
        var files = _ioPool.InvokeStream(ct => collection.GetFiles(path, ct))
            .Select(file => ScoreFile(collection, file, searchText, contextPath))
            .Where(item => item is not null)
            .Select(item => item!)
            .Catch<AutocompleteItem, Exception>(_ => Observable.Empty<AutocompleteItem>());

        var folders = _ioPool.InvokeStream(ct => collection.GetFolders(path, ct))
            .SelectMany(folder => WalkCollection(collection, folder.Path, searchText, contextPath))
            .Catch<AutocompleteItem, Exception>(_ => Observable.Empty<AutocompleteItem>());

        return files.Merge(folders);
    }

    /// <summary>
    /// Scores one file against the query and projects it to an <see cref="AutocompleteItem"/>,
    /// or returns null when the file doesn't match a non-empty query.
    /// </summary>
    private static AutocompleteItem? ScoreFile(
        ContentCollection collection, FileItem file, string searchText, string? contextPath)
    {
        var score = ScoreMatch(file.Name, searchText);
        if (score <= 0 && !string.IsNullOrEmpty(searchText))
            return null; // Skip non-matching files when there's a query

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

        return new AutocompleteItem(
            Label: file.Name,
            InsertText: insertText,
            Description: description,
            Category: collection.DisplayName,
            Priority: score,
            Kind: AutocompleteKind.File,
            Path: contentPath
        );
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

        // Exact filename match (including extension) is the strongest possible match —
        // the user typed the full file name. Bypass fuzzy scoring so typing "sample.docx"
        // doesn't score 0 just because "sample.docx" is longer than "sample".
        if (string.Equals(fileName, searchText, StringComparison.OrdinalIgnoreCase))
            return 3000;

        // Drop extension for scoring so "report" matches "report.md" without penalty.
        // Also drop the searchText extension if present — "sample.doc" should still match "sample.docx".
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var searchWithoutExt = Path.GetFileNameWithoutExtension(searchText);
        var effectiveSearch = string.IsNullOrEmpty(searchWithoutExt) ? searchText : searchWithoutExt;

        if (string.Equals(nameWithoutExt, effectiveSearch, StringComparison.OrdinalIgnoreCase))
            return 3000;

        // Use FuzzyScorer (case-insensitive, word-boundary aware)
        var scored = Scorer.Score(new[] { nameWithoutExt }, effectiveSearch, s => s).FirstOrDefault();
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
