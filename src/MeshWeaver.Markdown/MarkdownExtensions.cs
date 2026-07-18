using System.Collections.Concurrent;
using Markdig;

namespace MeshWeaver.Markdown;

/// <summary>
/// Factory and cache for the MeshWeaver Markdig pipeline (math, emoji, layout-area, image-path,
/// link-cleanup, and executable-code-block extensions), plus static-href helpers.
/// </summary>
public static class MarkdownExtensions
{
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

    /// <summary>
    /// Returns the configured Markdig pipeline for the given content collection and node path, served
    /// from a bounded cache (built once per distinct key — see the caching notes above).
    /// </summary>
    /// <param name="collection">The content collection used to build static image hrefs, or null.</param>
    /// <param name="currentNodePath">The current node path used to resolve relative references, or null.</param>
    /// <returns>A cached, immutable, reusable markdown pipeline.</returns>
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
            // Emoji :shortcodes: ONLY — ASCII smileys are disabled. The smiley parser turns any
            // colon adjacent to a trigger char (`:*`, `:D`, `:P`, `:|`, …) into an emoji, which
            // corrupts ordinary colon-dense markdown: `:**Batch**` → 😗 + broken bold, `:Die` → 😄,
            // and `|---:|` → 😐 broke right-aligned pipe tables (the 2026-06-13 table bug). Agent
            // output is especially colon-heavy (`Label:` + bold/capitalized words), so these false
            // positives are common. Disabling smileys removes the whole class in one line; real
            // emoji still work via unambiguous `:shortcode:` (`:smile:`, `:warning:`). Issue #402.
            .UseEmojiAndSmiley(enableSmileys: false)
            .UseYamlFrontMatter()
            .Use(new ImgPathMarkdownExtension(path => ToStaticHref(path, collection)))
            .Use(new LinkUrlCleanupExtension(currentNodePath))
            .Use(new LayoutAreaMarkdownExtension(currentNodePath))
            .Use(new ExecutableCodeBlockExtension())
            .Build();

    /// <summary>
    /// Builds the static-content href for an asset path within a collection (<c>static/{collection}/{path}</c>).
    /// </summary>
    /// <param name="path">The asset path relative to the collection.</param>
    /// <param name="collection">The content collection the asset belongs to.</param>
    /// <returns>The static href string.</returns>
    public static string ToStaticHref(string path, object? collection)
        => $"static/{collection}/{path}";

    /// <summary>
    /// Test hook — clears the pipeline cache. Production code never calls this.
    /// </summary>
    public static void ClearPipelineCache() => PipelineCache.Clear();
}
