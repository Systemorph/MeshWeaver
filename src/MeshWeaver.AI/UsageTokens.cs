using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Canonical <see cref="UsageDetails.AdditionalCounts"/> keys and a provider-agnostic
/// reader for prompt-cache token counts.
///
/// <para>Prompt caching is reported differently per provider, and the raw counters used to be
/// dropped on the floor (the "in / out" counter under-reported the real, billable context):</para>
/// <list type="bullet">
///   <item><b>Anthropic wire</b> (native / Azure Foundry Claude): <c>input_tokens</c>
///   EXCLUDES cached tokens; the cached portion is reported separately as
///   <c>cache_read_input_tokens</c> / <c>cache_creation_input_tokens</c>. The Claude client
///   NORMALISES to the OpenAI convention (folds cache read + creation into the input total) and
///   writes the breakdown into <see cref="UsageDetails.AdditionalCounts"/> under the keys below.</item>
///   <item><b>OpenAI wire</b> (OpenAI / OpenRouter / Azure OpenAI / Azure Foundry via
///   <c>Microsoft.Extensions.AI.OpenAI</c>): <c>prompt_tokens</c> already INCLUDES cached reads, and
///   the adapter surfaces the cached subset in <c>AdditionalCounts["InputTokenDetails.CachedTokenCount"]</c>.</item>
/// </list>
///
/// <para>So after normalisation there is ONE consistent meaning everywhere: <c>InputTokenCount</c> is
/// the FULL prompt-token count, and cache read / write are SUBSETS of it. <see cref="SplitCache"/>
/// matches both the OpenAI adapter's key and the Claude client's keys by substring, so it works
/// regardless of which provider produced the usage.</para>
/// </summary>
public static class UsageTokens
{
    /// <summary>Cache-READ (cache-hit) prompt tokens — a subset of <see cref="UsageDetails.InputTokenCount"/>.</summary>
    public const string CacheReadKey = "CacheReadInputTokens";

    /// <summary>Cache-WRITE (cache-creation) prompt tokens — a subset of <see cref="UsageDetails.InputTokenCount"/>.</summary>
    public const string CacheWriteKey = "CacheCreationInputTokens";

    /// <summary>
    /// Extracts (cacheRead, cacheWrite) prompt-token counts from a provider's
    /// <see cref="UsageDetails.AdditionalCounts"/>, matching the Claude client's keys AND the
    /// OpenAI adapter's <c>InputTokenDetails.CachedTokenCount</c> — case-insensitively by substring.
    /// Returns <c>(0, 0)</c> when the provider reported no cache detail.
    /// </summary>
    public static (long CacheRead, long CacheWrite) SplitCache(UsageDetails? details)
    {
        long read = 0, write = 0;
        if (details?.AdditionalCounts is { } counts)
        {
            foreach (var kv in counts)
            {
                // Order matters: "CacheCreation"/"CacheWrite" before the generic "Cache" (which
                // also matches OpenAI's "Cached...") so a write is never miscounted as a read.
                if (kv.Key.Contains("CacheCreation", StringComparison.OrdinalIgnoreCase)
                    || kv.Key.Contains("CacheWrite", StringComparison.OrdinalIgnoreCase))
                    write += kv.Value;
                else if (kv.Key.Contains("Cache", StringComparison.OrdinalIgnoreCase))
                    read += kv.Value;
            }
        }
        return (read, write);
    }
}
