using System.Collections.Immutable;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Pure, deterministic character-window chunker. No I/O — it only slices a string, so it is
/// CPU-only and may be invoked inside the extractor's <c>InvokeBlocking</c> leaf or on its own.
/// </summary>
public static class TextChunker
{
    /// <summary>A half-open character window <c>[Start, Start+Length)</c> of the source text.</summary>
    private readonly record struct TextWindow(int Start, int Length)
    {
        public int End => Start + Length;
    }

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

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var window in Windows(text!.Length, size, overlap))
            builder.Add(text.Substring(window.Start, window.Length));
        return builder.ToImmutable();
    }

    /// <summary>
    /// Chunks the extracted document into the same character windows as <see cref="Chunk"/>, but attributes
    /// each chunk to its source <b>page</b> and <b>on-page box</b> using the document's positional spans.
    /// A chunk is attributed to the page where it <i>begins</i> (the first overlapping span's page), and its
    /// <see cref="PositionedChunk.Position"/> is the union of every span on that page inside the window — so
    /// the box covers exactly the chunk's content on its starting page, even across a line break. When the
    /// document has no spans (txt/markdown/docx) every chunk carries a null page/position.
    /// </summary>
    /// <param name="document">The extracted text + positional spans (from <see cref="ITextExtractor.ExtractDocument"/>).</param>
    /// <param name="size">Window size in characters. Must be positive.</param>
    /// <param name="overlap">Characters shared between consecutive windows; clamped into <c>[0, size)</c>.</param>
    /// <returns>The ordered positioned chunks (empty when the document text is empty).</returns>
    public static IReadOnlyList<PositionedChunk> ChunkPositioned(ExtractedDocument document, int size, int overlap)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (size < 1)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Chunk size must be positive.");

        var text = document.Text;
        if (string.IsNullOrEmpty(text))
            return ImmutableArray<PositionedChunk>.Empty;

        var spans = document.Spans;
        var builder = ImmutableArray.CreateBuilder<PositionedChunk>();

        // Spans are ordered by Start and windows advance monotonically, so a single cursor over the spans
        // suffices — advance it past every span that ends at/before the current window's start (those can
        // never overlap this or any later window).
        var cursor = 0;
        foreach (var window in Windows(text.Length, size, overlap))
        {
            while (cursor < spans.Length && spans[cursor].End <= window.Start)
                cursor++;

            int? page = null;
            ChunkPosition? position = null;
            for (var i = cursor; i < spans.Length && spans[i].Start < window.End; i++)
            {
                var span = spans[i];
                if (span.End <= window.Start)
                    continue; // fully before the window (defensive; cursor should have skipped it)
                if (page is null)
                {
                    page = span.Page;
                    position = span.Box;
                }
                else if (span.Page == page)
                {
                    position = position!.Union(span.Box);
                }
                // Spans on a page after the chunk's start page are left out of the box — the chunk is
                // attributed to where it begins.
            }

            builder.Add(new PositionedChunk(text.Substring(window.Start, window.Length), page, position));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// The ordered character windows for a text of <paramref name="textLength"/>: fixed <paramref name="size"/>
    /// windows advancing by <c>size - overlap</c>, the last one truncated to the text end. Shared by both
    /// <see cref="Chunk"/> and <see cref="ChunkPositioned"/> so the two never drift.
    /// </summary>
    private static IEnumerable<TextWindow> Windows(int textLength, int size, int overlap)
    {
        if (textLength <= 0)
            yield break;

        // Clamp overlap into [0, size-1] so the stride (size - overlap) is always >= 1 — a stride of 0
        // would loop forever. Guard, not band-aid: a pure function must terminate for every input.
        if (overlap < 0)
            overlap = 0;
        if (overlap >= size)
            overlap = size - 1;

        // Shorter than one window → exactly one chunk (the whole text).
        if (textLength <= size)
        {
            yield return new TextWindow(0, textLength);
            yield break;
        }

        var stride = size - overlap;
        for (var start = 0; start < textLength; start += stride)
        {
            var length = Math.Min(size, textLength - start);
            yield return new TextWindow(start, length);

            // The final window reaches the end of the text — stop so we never emit a trailing duplicate
            // sub-window covering only the overlap tail.
            if (start + length >= textLength)
                yield break;
        }
    }
}
