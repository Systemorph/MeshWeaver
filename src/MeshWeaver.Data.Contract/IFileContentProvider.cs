namespace MeshWeaver.Data;

/// <summary>
/// Interface for providing file content from content collections.
/// All operations return <see cref="IObservable{T}"/>; implementations bridge to
/// filesystem I/O at this boundary (the innermost async edge).
/// </summary>
public interface IFileContentProvider
{
    /// <summary>
    /// Gets file content as a string. Emits a single result when the I/O completes.
    /// </summary>
    IObservable<FileContentResult> GetFileContent(
        string collectionName,
        string filePath,
        int? numberOfRows = null);

    /// <summary>
    /// Saves content to a file. Emits a single result when the I/O completes.
    /// </summary>
    IObservable<FileOperationResult> SaveFileContent(
        string collectionName,
        string filePath,
        Stream content);

    /// <summary>
    /// Deletes a file. Emits a single result when the I/O completes.
    /// </summary>
    IObservable<FileOperationResult> DeleteFile(
        string collectionName,
        string filePath);

    /// <summary>
    /// Lists files and folders in a content collection path. Emits a single result when listing completes.
    /// </summary>
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
    public IReadOnlyCollection<CollectionItemInfo>? Items { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null && Items != null;

    public static CollectionListingResult Ok(IReadOnlyCollection<CollectionItemInfo> items) => new() { Items = items };
    public static CollectionListingResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Result of a file content retrieval operation.
/// </summary>
public record FileContentResult
{
    public string? Content { get; init; }
    public string? Error { get; init; }
    public bool Success => Error == null && Content != null;

    public static FileContentResult Ok(string content) => new() { Content = content };
    public static FileContentResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Result of a file operation (save, delete, etc.).
/// </summary>
public record FileOperationResult
{
    public string? Error { get; init; }
    public bool Success => Error == null;

    public static FileOperationResult Ok() => new();
    public static FileOperationResult Fail(string error) => new() { Error = error };
}
