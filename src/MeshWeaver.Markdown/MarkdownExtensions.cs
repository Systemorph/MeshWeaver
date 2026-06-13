using System.Collections.Concurrent;
using System.Linq;
using Markdig;
using Markdig.Extensions.Emoji;

namespace MeshWeaver.Markdown;

public static class MarkdownExtensions
{
    // Emoji + smiley mapping that keeps EVERY emoji :shortcode: and EVERY ASCII smiley
    // EXCEPT the ones whose trigger contains '|' (`:|` and `:-|` → 😐). Those corrupt a
    // pipe table's right/center alignment delimiter — `|---:|` becomes `|---😐`, so Markdig
    // no longer recognises the table and renders the rows as a literal `<p>` (the
    // 2026-06-13 "balance-sheet table shows as one inline line" bug — the 😐 in the
    // separator was the tell). Dropping only the pipe-bearing smileys keeps `:)` → 🙂 etc.
    // working while letting right-aligned tables render. Immutable, built once.
    private static readonly EmojiMapping TableSafeEmojiMapping = new(
        EmojiMapping.GetDefaultEmojiShortcodeToUnicode(),
        EmojiMapping.GetDefaultSmileyToEmojiShortcode()
            .Where(kv => !kv.Key.Contains('|'))
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    // Building a MarkdownPipeline costs ~350µs (block/inline parser registration, sort by
    // priority, etc). For small docs that's ~60% of the entire render time. Markdig is
    // explicit that pipelines are designed to be cached and reused — the parsing path
    // creates a fresh BlockProcessor/InlineProcessor per call, so the pipeline itself is
    // immutable after Build().
    //
    // Key is (collection.ToString(), currentNodePath) because the only places those
    // parameters reach are ImgPathMarkdownExtension (uses $"static/{collection}/{path}")
    // and LinkUrlCleanup/LayoutAreaMarkdownExtension (string compare on path).
    //
    // Cap the cache so a long-running portal that renders many distinct nodes doesn't
    // accumulate pipelines indefinitely. When the cap is hit we drop the whole cache
    // rather than implement LRU — keeps the code trivial and the hot paths in any
    // session churn only a handful of distinct keys.
    private const int CacheCapacity = 1024;

    private static readonly ConcurrentDictionary<(string?, string?), MarkdownPipeline> PipelineCache = new();

    public static MarkdownPipeline CreateMarkdownPipeline(
        object? collection,
        string? currentNodePath = null)
    {
        var key = (collection?.ToString(), currentNodePath);

        if (PipelineCache.TryGetValue(key, out var cached))
            return cached;

        if (PipelineCache.Count >= CacheCapacity)
            PipelineCache.Clear();

        return PipelineCache.GetOrAdd(key, _ => BuildPipeline(collection, currentNodePath));
    }

    private static MarkdownPipeline BuildPipeline(object? collection, string? currentNodePath) =>
        new MarkdownPipelineBuilder()
            .UseMathematics()
            .UseAdvancedExtensions()
            .UseGenericAttributes()
            // Custom mapping: all emoji + all smileys EXCEPT the pipe-bearing ones that
            // break table alignment delimiters (see TableSafeEmojiMapping above).
            .UseEmojiAndSmiley(TableSafeEmojiMapping)
            .UseYamlFrontMatter()
            .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, collection)))
            .Use(new LinkUrlCleanupExtension(currentNodePath))
            .Use(new LayoutAreaMarkdownExtension(currentNodePath))
            .Use(new ExecutableCodeBlockExtension())
            .Build();

    public static string ToStaticHref(string path, object? collection)
        => $"static/{collection}/{path}";

    /// <summary>
    /// Test hook — clears the pipeline cache. Production code never calls this.
    /// </summary>
    public static void ClearPipelineCache() => PipelineCache.Clear();
}
