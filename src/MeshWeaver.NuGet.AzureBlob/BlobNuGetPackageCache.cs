using System.IO.Compression;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.NuGet.AzureBlob;

/// <summary>
/// Azure Blob Storage-backed persistent cache for NuGet packages.
/// Each package version is stored as a single .zip blob at {container}/{id-lower}/{version}.zip,
/// containing the entire contents of the NuGet global-packages subfolder for that version.
/// Hydrate downloads and extracts it; Save zips and uploads.
/// </summary>
public sealed class BlobNuGetPackageCache : INuGetPackageCache
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobNuGetPackageCache> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public BlobNuGetPackageCache(BlobServiceClient blobService, string containerName, ILogger<BlobNuGetPackageCache> logger)
    {
        _container = blobService.GetBlobContainerClient(containerName);
        _logger = logger;
    }

    public async Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(BlobName(packageId, version));
        try
        {
            await using var stream = await blob.OpenReadAsync(cancellationToken: ct);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            Directory.CreateDirectory(targetPackageFolder);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                var destPath = Path.GetFullPath(Path.Combine(targetPackageFolder, entry.FullName));
                if (!destPath.StartsWith(Path.GetFullPath(targetPackageFolder), StringComparison.OrdinalIgnoreCase))
                    continue; // zip slip guard
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await using var outStream = File.Create(destPath);
                await using var inStream = entry.Open();
                await inStream.CopyToAsync(outStream, ct);
            }
            _logger.LogDebug("Hydrated {Id} {Version} from blob", packageId, version);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hydrate failed for {Id} {Version}; will fall back to feed", packageId, version);
            return false;
        }
    }

    public async Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct)
    {
        if (!Directory.Exists(sourcePackageFolder)) return;
        await EnsureContainerAsync(ct);

        var blob = _container.GetBlobClient(BlobName(packageId, version));
        // Skip if already saved — many replicas will try to save the same package concurrently.
        if (await blob.ExistsAsync(ct)) return;

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = Path.GetFullPath(sourcePackageFolder);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var entry = archive.CreateEntry(rel, CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                await using var fileStream = File.OpenRead(file);
                await fileStream.CopyToAsync(entryStream, ct);
            }
        }
        buffer.Position = 0;
        try
        {
            await blob.UploadAsync(buffer, overwrite: false, ct);
            _logger.LogInformation("Saved {Id} {Version} to blob ({Size} bytes)", packageId, version, buffer.Length);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Another replica won the race; fine.
        }
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private static string BlobName(string packageId, string version) =>
        $"{packageId.ToLowerInvariant()}/{version}.zip";
}
