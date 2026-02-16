namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Service for importing nodes and content into the mesh, and deleting content.
/// </summary>
public interface IMeshImportService
{
    /// <summary>
    /// Imports nodes from a file system source directory into the mesh storage.
    /// </summary>
    /// <param name="sourcePath">Server-side source directory path</param>
    /// <param name="targetRootPath">Target root path in the mesh (empty for root)</param>
    /// <param name="force">If true, re-import even if data already exists</param>
    /// <param name="onProgress">Optional progress callback (nodesImported, partitionsImported, currentPath)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import result with counts and timing</returns>
    Task<ImportNodesResponse> ImportNodesAsync(
        string sourcePath,
        string? targetRootPath = null,
        bool force = false,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Imports content files from a source directory into a content collection.
    /// </summary>
    /// <param name="collectionName">Name of the content collection</param>
    /// <param name="sourceDirectory">Server-side source directory</param>
    /// <param name="targetFolder">Target folder within the collection</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of files imported</returns>
    Task<int> ImportContentAsync(
        string collectionName,
        string sourceDirectory,
        string targetFolder,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all content in a folder within a content collection.
    /// </summary>
    /// <param name="collectionName">Name of the content collection</param>
    /// <param name="folderPath">Folder path to delete</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Number of items deleted</returns>
    Task<int> DeleteContentFolderAsync(
        string collectionName,
        string folderPath,
        CancellationToken ct = default);
}
