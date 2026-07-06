using System.Collections.Immutable;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// A run of extracted text that maps a character range in <see cref="ExtractedDocument.Text"/> to the
/// source <see cref="Page"/> it came from and its <see cref="Box"/> on that page. For a PDF, one span per
/// extracted word — so any character window (a chunk) resolves to the page(s) and box(es) it overlaps.
/// </summary>
/// <param name="Start">Zero-based start offset of the run within <see cref="ExtractedDocument.Text"/>.</param>
/// <param name="Length">Length of the run in characters.</param>
/// <param name="Page">One-based source page number the run was extracted from.</param>
/// <param name="Box">The run's normalized bounding box on <see cref="Page"/>.</param>
public sealed record PositionedSpan(int Start, int Length, int Page, ChunkPosition Box)
{
    /// <summary>The exclusive end offset (<see cref="Start"/> + <see cref="Length"/>).</summary>
    public int End => Start + Length;
}

/// <summary>
/// The result of extracting a content file: the plain <see cref="Text"/> that gets chunked and embedded,
/// plus optional positional <see cref="Spans"/> that pin ranges of that text back to their source page and
/// on-page box. <see cref="Spans"/> is empty for formats with no layout (txt/markdown/docx) — those chunks
/// carry no page/position. For PDFs it is dense (one span per word), so <see cref="TextChunker.ChunkPositioned"/>
/// can attribute each chunk to a page + region.
/// </summary>
/// <param name="Text">The extracted document text (unchanged in role — this is what is chunked/embedded).</param>
/// <param name="Spans">
/// Positional spans over <see cref="Text"/>, ordered by <see cref="PositionedSpan.Start"/>. Empty when the
/// source format carries no layout.
/// </param>
public sealed record ExtractedDocument(string Text, ImmutableArray<PositionedSpan> Spans)
{
    /// <summary>An empty extraction (no text, no spans) — the result for unknown/binary/empty files.</summary>
    public static readonly ExtractedDocument Empty = new(string.Empty, ImmutableArray<PositionedSpan>.Empty);

    /// <summary>Wraps plain text with no positional information (the txt/markdown/docx case).</summary>
    public static ExtractedDocument PlainText(string text) =>
        string.IsNullOrEmpty(text) ? Empty : new(text, ImmutableArray<PositionedSpan>.Empty);
}

/// <summary>
/// One chunk produced by <see cref="TextChunker.ChunkPositioned"/>: the chunk <see cref="Text"/> plus the
/// source <see cref="Page"/> it begins on and its <see cref="Position"/> (box) on that page. <see cref="Page"/>
/// and <see cref="Position"/> are null when the source carries no layout (txt/markdown/docx).
/// </summary>
/// <param name="Text">The chunk text (a character window of the extracted document text).</param>
/// <param name="Page">One-based page the chunk begins on, or null when unknown.</param>
/// <param name="Position">The chunk's normalized box on <see cref="Page"/>, or null when unknown.</param>
public sealed record PositionedChunk(string Text, int? Page, ChunkPosition? Position);
