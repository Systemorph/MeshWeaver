using System.Collections.Concurrent;
using Markdig;

namespace MeshWeaver.Markdown;

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
            .UseEmojiAndSmiley()
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
