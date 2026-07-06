using System.Collections.Immutable;
using System.Text;
using DocSharp.Docx;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Default <see cref="ITextExtractor"/>. Dispatches by file extension and runs the actual
/// decode/parse as a CPU leaf through <see cref="IIoPool.InvokeBlocking{T}"/> — never on the
/// calling hub scheduler, bounded by the pool. Unknown / binary formats extract to empty (logged).
///
/// <para>For PDFs, extraction is <b>positional</b>: the text is built word-by-word from PdfPig's laid-out
/// words, and each word becomes a <see cref="PositionedSpan"/> (page + normalized on-page box), so a chunk
/// can be pinned back to the page + region it came from. Other formats extract to plain text (no spans).</para>
/// </summary>
public sealed class TextExtractor : ITextExtractor
{
    private readonly IIoPool _ioPool;
    private readonly ILogger<TextExtractor> _logger;

    /// <summary>
    /// Creates a text extractor. <paramref name="ioPool"/> defaults to
    /// <see cref="IoPool.Unbounded"/> (ThreadPool offload, no cap) when constructed outside DI,
    /// e.g. in tests.
    /// </summary>
    public TextExtractor(IIoPool? ioPool = null, ILogger<TextExtractor>? logger = null)
    {
        _ioPool = ioPool ?? IoPool.Unbounded;
        _logger = logger ?? NullLogger<TextExtractor>.Instance;
    }

    /// <summary>
    /// Extracts plain text from the file bytes, dispatching by the file extension and running the
    /// CPU-bound parse off the calling scheduler via the I/O pool. Unknown/binary formats yield empty text.
    /// </summary>
    /// <param name="fileName">The file name; its extension selects the decoder.</param>
    /// <param name="bytes">The raw file content to decode.</param>
    /// <returns>A cold observable emitting the extracted text (empty for unsupported formats).</returns>
    public IObservable<string> ExtractText(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        // The whole parse is a synchronous, CPU-bound leaf — PdfPig, DocSharp and StreamReader all
        // block. InvokeBlocking runs it on the limited-concurrency scheduler, off the hub thread.
        return _ioPool.InvokeBlocking(_ => Extract(extension, fileName, bytes).Text);
    }

    /// <inheritdoc/>
    public IObservable<ExtractedDocument> ExtractDocument(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return _ioPool.InvokeBlocking(_ => Extract(extension, fileName, bytes));
    }

    private ExtractedDocument Extract(string extension, string fileName, byte[] bytes)
    {
        switch (extension)
        {
            case ".pdf":
                return ExtractPdf(bytes);
            case ".docx":
                return ExtractedDocument.PlainText(ExtractDocx(bytes));
            case ".md":
            case ".markdown":
            case ".txt":
            case ".csv":
            case ".json":
            case ".html":
            case ".htm":
            case ".xml":
                return ExtractedDocument.PlainText(DecodeUtf8(bytes));
            default:
                _logger.LogInformation(
                    "No text extractor for extension '{Extension}' (file '{FileName}'); extracting to empty.",
                    extension, fileName);
                return ExtractedDocument.Empty;
        }
    }

    /// <summary>
    /// Extracts a PDF word-by-word (PdfPig's laid-out words, in reading order): builds the text as words
    /// joined by spaces within a page and newlines between pages, and records one <see cref="PositionedSpan"/>
    /// per word (its page + normalized top-left-origin box). So any character window of the resulting text
    /// resolves to the page(s) + region(s) it covers.
    /// </summary>
    private static ExtractedDocument ExtractPdf(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        var sb = new StringBuilder();
        var spans = ImmutableArray.CreateBuilder<PositionedSpan>();

        foreach (var page in document.GetPages())
        {
            var pageWidth = page.Width;
            var pageHeight = page.Height;
            if (sb.Length > 0)
                sb.Append('\n');

            var first = true;
            foreach (var word in page.GetWords())
            {
                var text = word.Text;
                if (string.IsNullOrEmpty(text))
                    continue;
                if (!first)
                    sb.Append(' ');
                first = false;

                var start = sb.Length;
                sb.Append(text);

                // Skip positioning on degenerate page sizes rather than dividing by zero — the word's
                // text is still indexed, just without a resolvable box.
                if (pageWidth > 0 && pageHeight > 0)
                    spans.Add(new PositionedSpan(
                        start, text.Length, page.Number, NormalizeBox(word.BoundingBox, pageWidth, pageHeight)));
            }
        }

        return new ExtractedDocument(sb.ToString(), spans.ToImmutable());
    }

    /// <summary>
    /// Normalizes a PdfPig word bounding box (PDF space: points, origin bottom-left, y up) to a
    /// <see cref="ChunkPosition"/> (fractions of the page, origin top-left, y down) so a viewer can overlay
    /// the highlight at any render scale. Uses min/max so a rotated/flipped rectangle still yields a
    /// positive box, and clamps to <c>[0, 1]</c>.
    /// </summary>
    private static ChunkPosition NormalizeBox(UglyToad.PdfPig.Core.PdfRectangle box, double pageWidth, double pageHeight)
    {
        var leftPt = Math.Min(box.Left, box.Right);
        var rightPt = Math.Max(box.Left, box.Right);
        var topPt = Math.Max(box.Top, box.Bottom);      // higher PDF y = visual top
        var bottomPt = Math.Min(box.Top, box.Bottom);

        var x = Clamp01(leftPt / pageWidth);
        var width = Clamp01((rightPt - leftPt) / pageWidth);
        var y = Clamp01(1.0 - topPt / pageHeight);       // flip to top-left origin
        var height = Clamp01((topPt - bottomPt) / pageHeight);
        return new ChunkPosition(x, y, width, height);
    }

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    private static string ExtractDocx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var converter = new DocxToMarkdownConverter();
        return converter.ConvertToString(stream);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        // detectEncodingFromByteOrderMarks: true honours a BOM if present, else UTF-8.
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
