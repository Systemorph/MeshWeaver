using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Implementation of IMeshImportService that imports nodes and content
/// from file system sources into the mesh storage.
/// Scoped service — uses the hub's JsonSerializerOptions for proper type polymorphism.
/// </summary>
public class MeshImportService : IMeshImportService
{
    private readonly IStorageAdapter? _storageAdapter;
    private readonly IContentService _contentService;
    private readonly IMessageHub _hub;
    private readonly ILogger<MeshImportService> _logger;

    public MeshImportService(
        IContentService contentService,
        IMessageHub hub,
        ILogger<MeshImportService> logger,
        IStorageAdapter? storageAdapter = null)
    {
        _storageAdapter = storageAdapter;
        _contentService = contentService;
        _hub = hub;
        _logger = logger;
    }

    public async Task<ImportNodesResponse> ImportNodesAsync(
        string sourcePath,
        string? targetRootPath = null,
        bool force = false,
        bool removeMissing = false,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
            return ImportNodesResponse.Fail($"Source path does not exist: {sourcePath}");

        try
        {
            if (_storageAdapter == null)
                return ImportNodesResponse.Fail("Import is not available: no storage adapter configured.");

            var source = new FileSystemStorageAdapter(sourcePath);
            return await ImportHelper.RunImportAsync(
                source, _storageAdapter, _logger, force, targetRootPath, removeMissing, onProgress, ct,
                _hub.JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import nodes from {SourcePath}", sourcePath);
            return ImportNodesResponse.Fail($"Import failed: {ex.Message}");
        }
    }

    public async Task<int> ImportContentAsync(
        string collectionName,
        string sourceDirectory,
        string targetFolder,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceDirectory}");

        var collection = await _contentService.GetCollectionAsync(collectionName, ct)
            ?? throw new InvalidOperationException($"Content collection '{collectionName}' not found");

        var files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
        var imported = 0;

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var targetPath = string.IsNullOrEmpty(targetFolder)
                ? Path.GetDirectoryName(relativePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? ""
                : $"{targetFolder}/{Path.GetDirectoryName(relativePath)?.Replace(Path.DirectorySeparatorChar, '/')}".TrimEnd('/');
            var fileName = Path.GetFileName(filePath);

            await using var stream = File.OpenRead(filePath);
            await collection.SaveFileAsync(targetPath, fileName, stream);
            imported++;

            _logger.LogDebug("Imported content file {FileName} to {TargetPath}/{Collection}",
                fileName, targetPath, collectionName);
        }

        _logger.LogInformation("Imported {Count} files into collection '{Collection}'",
            imported, collectionName);
        return imported;
    }

    public async Task<int> DeleteContentFolderAsync(
        string collectionName,
        string folderPath,
        CancellationToken ct = default)
    {
        var collection = await _contentService.GetCollectionAsync(collectionName, ct)
            ?? throw new InvalidOperationException($"Content collection '{collectionName}' not found");

        // Count items before deletion
        var files = await collection.GetFilesAsync(folderPath);
        var folders = await collection.GetFoldersAsync(folderPath);
        var itemCount = files.Count + folders.Count;

        await collection.DeleteFolderAsync(folderPath);

        _logger.LogInformation("Deleted folder '{FolderPath}' from collection '{Collection}' ({Count} items)",
            folderPath, collectionName, itemCount);

        return itemCount;
    }
}
