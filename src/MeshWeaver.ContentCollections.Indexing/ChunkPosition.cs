namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// The location of a chunk on its source page, as a <b>normalized</b> bounding box: every coordinate is a
/// fraction of the page in <c>[0, 1]</c> with a <b>top-left origin</b> (the UI/screen convention — x grows
/// right, y grows down). Normalization is deliberate: a viewer can overlay a highlight rectangle at any
/// render scale (a thumbnail, a full page, a zoomed page) without knowing the source page's point size.
///
/// <para>Produced by <see cref="ITextExtractor"/> from the source document's own text-layout coordinates
/// (for PDFs, PdfPig word bounding boxes) and carried on a <see cref="ContentChunk"/> so a consumer can
/// literally open the source page and mark the region a chunk (or an extracted value) came from. Null when
/// the source format carries no layout (txt/markdown/docx) or the position could not be resolved.</para>
/// </summary>
/// <param name="X">Left edge as a fraction of page width, <c>[0, 1]</c>.</param>
/// <param name="Y">Top edge as a fraction of page height, <c>[0, 1]</c> (top-left origin).</param>
/// <param name="Width">Box width as a fraction of page width, <c>[0, 1]</c>.</param>
/// <param name="Height">Box height as a fraction of page height, <c>[0, 1]</c>.</param>
public sealed record ChunkPosition(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// The smallest box that contains both this and <paramref name="other"/> — used to grow a chunk's box
    /// to cover every word (span) that falls inside its text window on the same page.
    /// </summary>
    public ChunkPosition Union(ChunkPosition other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(X + Width, other.X + other.Width);
        var bottom = Math.Max(Y + Height, other.Y + other.Height);
        return new ChunkPosition(left, top, right - left, bottom - top);
    }
}
