namespace MeshWeaver.Data;

/// <summary>
/// Interface for providing file content from content collections.
/// This abstraction allows MeshWeaver.Data to access file content without
/// directly referencing MeshWeaver.ContentCollections.
/// </summary>
public interface IFileContentProvider
{
    /// <summary>
    /// Gets the content of a file as a string.
    /// </summary>
    /// <param name="collectionName">The name of the content collection</param>
    /// <param name="filePath">The path to the file within the collection</param>
    /// <param name="numberOfRows">Optional: number of lines to read for text files</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The file content as a string, or null if not found</returns>
    Task<FileContentResult> GetFileContentAsync(
        string collectionName,
        string filePath,
        int? numberOfRows = null,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a file content retrieval operation.
/// </summary>
public record FileContentResult
{
    /// <summary>
    /// The file content as a string.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Error message if the retrieval failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// True if the retrieval was successful.
    /// </summary>
    public bool Success => Error == null && Content != null;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static FileContentResult Ok(string content) => new() { Content = content };

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static FileContentResult Fail(string error) => new() { Error = error };
}
