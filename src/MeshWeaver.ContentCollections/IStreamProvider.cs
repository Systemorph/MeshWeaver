using System.Collections.Immutable;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Provides stream access for static content from various sources
/// </summary>
public interface IStreamProvider
{
    /// <summary>
    /// The provider type identifier (e.g., "FileSystem", "AzureBlob")
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Gets a stream for reading content from the provider
    /// </summary>
    /// <param name="reference">Provider-specific reference (e.g., file path, blob URL)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stream to read content, or null if not found</returns>
    Task<Stream?> GetStreamAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a stream with metadata (path and last modified time)
    /// </summary>
    Task<(Stream? Stream, string Path, DateTime LastModified)> GetStreamWithMetadataAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes content to the provider
    /// </summary>
    /// <param name="reference">Provider-specific reference (e.g., file path, blob URL)</param>
    /// <param name="content">Stream containing the content to write</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteStreamAsync(string reference, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple streams matching a filter
    /// </summary>
    IAsyncEnumerable<(Stream? Stream, string Path, DateTime LastModified)> GetStreamsAsync(Func<string, bool> filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all folders in the specified path
    /// </summary>
    Task<IReadOnlyCollection<FolderItem>> GetFoldersAsync(string path);

    /// <summary>
    /// Gets all files in the specified path
    /// </summary>
    Task<IReadOnlyCollection<FileItem>> GetFilesAsync(string path);

    /// <summary>
    /// Saves a file to the provider
    /// </summary>
    Task SaveFileAsync(string path, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a folder
    /// </summary>
    Task CreateFolderAsync(string folderPath);

    /// <summary>
    /// Deletes a folder
    /// </summary>
    Task DeleteFolderAsync(string folderPath);

    /// <summary>
    /// Deletes a file
    /// </summary>
    Task DeleteFileAsync(string filePath);

    /// <summary>
    /// Attaches a monitor to watch for changes. Returns a disposable to stop monitoring.
    /// </summary>
    IDisposable? AttachMonitor(Action<string> onChanged);

    /// <summary>
    /// Loads authors metadata
    /// </summary>
    Task<ImmutableDictionary<string, Author>> LoadAuthorsAsync(CancellationToken cancellationToken = default);
}
