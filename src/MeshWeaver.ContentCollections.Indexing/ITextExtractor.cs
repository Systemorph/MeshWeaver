using System.Reactive.Linq;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Extracts plain text from a content file's raw bytes, dispatched by file extension.
/// Reactive: the decode/parse work is a CPU leaf run through an <c>IIoPool</c>, so the public
/// surface returns <see cref="IObservable{T}"/> and never <c>Task</c>.
/// </summary>
public interface ITextExtractor
{
    /// <summary>
    /// Extracts the document text from <paramref name="bytes"/>, choosing the parser from
    /// <paramref name="fileName"/>'s extension. Emits a single string (empty for unknown/binary
    /// formats) then completes.
    /// </summary>
    IObservable<string> ExtractText(string fileName, byte[] bytes);

    /// <summary>
    /// Extracts the document text <b>together with positional spans</b> (page + on-page box per run),
    /// so chunks can carry page/position provenance. For formats with a text layout (PDF) the result's
    /// <see cref="ExtractedDocument.Spans"/> is dense; for plain formats it is empty and this degrades to
    /// <see cref="ExtractText"/>. Default implementation wraps <see cref="ExtractText"/> as plain text, so
    /// an extractor with no layout information (or a test double) need not override it.
    /// </summary>
    IObservable<ExtractedDocument> ExtractDocument(string fileName, byte[] bytes) =>
        ExtractText(fileName, bytes).Select(ExtractedDocument.PlainText);
}
