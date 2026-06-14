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
}
