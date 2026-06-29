using System.Collections.Immutable;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Pure, deterministic character-window chunker. No I/O — it only slices a string, so it is
/// CPU-only and may be invoked inside the extractor's <c>InvokeBlocking</c> leaf or on its own.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Splits <paramref name="text"/> into fixed-size character windows that overlap by
    /// <paramref name="overlap"/> characters. Deterministic for given inputs.
    /// </summary>
    /// <param name="text">The text to chunk. Null/empty produces no chunks.</param>
    /// <param name="size">Window size in characters. Must be positive.</param>
    /// <param name="overlap">
    /// Characters shared between consecutive windows. Must be in <c>[0, size)</c> so the window
    /// always advances; values outside that range are clamped.
    /// </param>
    /// <returns>
    /// The ordered list of chunk texts. Text shorter than one window yields a single chunk;
    /// empty/whitespace-only or null text yields an empty list.
    /// </returns>
    public static IReadOnlyList<string> Chunk(string? text, int size, int overlap)
    {
        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Chunk size must be positive.");

        if (string.IsNullOrEmpty(text))
            return ImmutableArray<string>.Empty;

        // Clamp overlap into [0, size-1] so the stride (size - overlap) is always >= 1 — a stride
        // of 0 would loop forever. This is a guard, not a band-aid: callers pass sane values, but a
        // pure function must terminate for every input.
        if (overlap < 0)
            overlap = 0;
        if (overlap >= size)
            overlap = size - 1;

        // Shorter than one window → exactly one chunk (the whole text).
        if (text.Length <= size)
            return ImmutableArray.Create(text);

        var stride = size - overlap;
        var builder = ImmutableArray.CreateBuilder<string>();
        for (var start = 0; start < text.Length; start += stride)
        {
            var length = Math.Min(size, text.Length - start);
            builder.Add(text.Substring(start, length));

            // The final window reaches the end of the text — stop so we never emit a trailing
            // duplicate sub-window covering only the overlap tail.
            if (start + length >= text.Length)
                break;
        }

        return builder.ToImmutable();
    }
}
