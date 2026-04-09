namespace MeshWeaver.ContentCollections;

/// <summary>
/// Transforms binary content (e.g., .docx, .pptx, .xlsx) to text/markdown.
/// Registered via DI and resolved by ContentCollection.GetContentAsTextAsync.
/// </summary>
public interface IContentTransformer
{
    /// <summary>
    /// File extensions this transformer handles (e.g., ".docx").
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    /// Converts the input stream to markdown text.
    /// </summary>
    Task<string> TransformToMarkdownAsync(Stream input, CancellationToken ct = default);
}
