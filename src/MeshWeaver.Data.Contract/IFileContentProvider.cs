namespace MeshWeaver.Data;

/// <summary>
/// Interface for providing file content from content collections.
/// This abstraction allows MeshWeaver.Data to access file content without
/// directly referencing MeshWeaver.ContentCollections. All operations expose
/// <see cref="IObservable{T}"/> so callers compose reactively end-to-end —
/// the implementation is the innermost async edge that bridges to filesystem I/O.
/// </summary>
public interface IFileContentProvider
{
    /// <summary>
    /// Gets the content of a file as a string. Emits a single result
    /// when the underlying I/O completes.
    /// </summary>
    /// <param name="collectionName">The name of the content collection</param>
    /// <param name="filePath">The path to the file within the collection</param>
    /// <param name="numberOfRows">Optional: number of lines to read for text files</param>
    IObservable<FileContentResult> GetFileContent(
        string collectionName,
        string filePath,
        int? numberOfRows = null);

    /// <summary>
    /// Saves content to a file. Emits a single result when the underlying I/O completes.
    /// </summary>
    /// <param name="collectionName">The name of the content collection</param>
    /// <param name="filePath">The path to the file within the collection</param>
    /// <param name="content">The content to save</param>
    IObservable<FileOperationResult> SaveFileContent(
        string collectionName,
        string filePath,
        Stream content);

    /// <summary>
    /// Deletes a file. Emits a single result when the underlying I/O completes.
    /// </summary>
    /// <param name="collectionName">The name of the content collection</param>
    /// <param name="filePath">The path to the file within the collection</param>
    IObservable<FileOperationResult> DeleteFile(
        string collectionName,
        string filePath);

    /// <summary>
    /// Lists files and folders in a content collection path. Emits a single result
    /// when the listing completes.
    /// </summary>
    /// <param name="collectionName">The name of the content collection</param>
    /// <param name="path">The path within the collection (use "/" for root)</param>
    IObservable<CollectionListingResult> ListCollectionItems(
        string collectionName,
        string path);
}

/// <summary>
/// Represents an item in a content collection listing (file or folder).
/// </summary>
public record CollectionItemInfo(string Path, string Name, bool IsFolder, DateTime? LastModified = null);

/// <summary>
/// Result of a collection listing operation.
/// </summary>
public record CollectionListingResult
{
    /// <summary>
    /// The items in the collection path.
    /// </summary>
    public IReadOnlyCollection<CollectionItemInfo>? Items { get; init; }

    /// <summary>
    /// Error message if the listing failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// True if the listing was successful.
    /// </summary>
    public bool Success => Error == null && Items != null;

    public static CollectionListingResult Ok(IReadOnlyCollection<CollectionItemInfo> items) => new() { Items = items };
    public static CollectionListingResult Fail(string error) => new() { Error = error };
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

/// <summary>
/// Result of a file operation (save, delete, etc.).
/// </summary>
public record FileOperationResult
{
    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// True if the operation was successful.
    /// </summary>
    public bool Success => Error == null;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static FileOperationResult Ok() => new();

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static FileOperationResult Fail(string error) => new() { Error = error };
}
