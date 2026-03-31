using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Shared import logic used by both CLI tools and the in-app import service.
/// </summary>
public static class ImportHelper
{
    /// <summary>
    /// Runs a node import from a file system source to a target storage adapter.
    /// Handles idempotency check, progress logging, and error handling.
    /// </summary>
    /// <param name="source">Source storage adapter (typically FileSystemStorageAdapter)</param>
    /// <param name="target">Target storage adapter</param>
    /// <param name="logger">Logger for progress and errors</param>
    /// <param name="force">If true, skip idempotency check</param>
    /// <param name="rootPath">Optional root path to import from</param>
    /// <param name="removeMissing">If true, delete target nodes not present in source</param>
    /// <param name="onProgress">Optional progress callback</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="jsonOptions">Optional JSON serializer options (from hub); falls back to StorageImporter.CreateFullImportOptions()</param>
    /// <returns>Import result</returns>
    public static async Task<ImportNodesResponse> RunImportAsync(
        IStorageAdapter source,
        IStorageAdapter target,
        ILogger logger,
        bool force = false,
        string? rootPath = null,
        bool removeMissing = false,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default,
        JsonSerializerOptions? jsonOptions = null)
    {
        // Idempotency check
        if (!force)
        {
            var (nodePaths, _) = await target.ListChildPathsAsync(null, ct);
            if (nodePaths.Any())
            {
                logger.LogInformation("Target storage already contains data. Use force=true to re-import.");
                return ImportNodesResponse.Ok(0, 0, 0, 0, TimeSpan.Zero);
            }
        }

        jsonOptions ??= StorageImporter.CreateFullImportOptions();
        var importer = new StorageImporter(source, target, logger);
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = rootPath,
            RemoveMissing = removeMissing,
            JsonOptions = jsonOptions,
            OnProgress = onProgress ?? ((nodes, partitions, path) =>
            {
                if (nodes % 25 == 0)
                    logger.LogInformation("Progress: {Nodes} nodes, {Partitions} partitions (current: {Path})",
                        nodes, partitions, path);
            })
        }, ct);

        return ImportNodesResponse.Ok(
            result.NodesImported,
            result.PartitionsImported,
            result.NodesSkipped,
            result.PartitionsSkipped,
            result.Elapsed,
            result.NodesRemoved);
    }
}
