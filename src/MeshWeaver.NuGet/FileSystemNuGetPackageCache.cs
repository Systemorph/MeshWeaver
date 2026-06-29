using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.NuGet;

/// <summary>
/// Filesystem-backed persistent NuGet package cache — the Azure-free counterpart of
/// <c>BlobNuGetPackageCache</c>. Each package version is stored as a single .zip at
/// <c>{root}/{id-lower}/{version}.zip</c>. For single-node self-host the root is a local
/// volume; for HA it is a shared volume (NFS/CIFS) so any replica hydrates without
/// re-downloading from the feed. Writes go to a unique temp file then atomically move
/// into place, so a concurrent reader on a shared volume never opens a half-written zip.
/// </summary>
public sealed class FileSystemNuGetPackageCache : INuGetPackageCache
{
    private readonly string _root;
    private readonly ILogger<FileSystemNuGetPackageCache> _logger;

    /// <summary>
    /// Creates a filesystem-backed package cache rooted at <paramref name="root"/>. The root
    /// directory is resolved to an absolute path and created if it does not already exist.
    /// </summary>
    /// <param name="root">The directory under which cached package archives are stored. May be a
    /// local volume (single node) or a shared volume (HA) so any replica can hydrate from it.</param>
    /// <param name="logger">Logger used to record hydrate/save outcomes.</param>
    public FileSystemNuGetPackageCache(string root, ILogger<FileSystemNuGetPackageCache> logger)
    {
        _root = Path.GetFullPath(root);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Attempts to restore a previously cached package into <paramref name="targetPackageFolder"/>
    /// by extracting its archive. Returns <c>false</c> (without throwing) when the archive is
    /// missing or extraction fails, so the caller can fall back to the live feed.
    /// </summary>
    /// <param name="packageId">The package id to hydrate.</param>
    /// <param name="version">The exact package version to hydrate.</param>
    /// <param name="targetPackageFolder">The destination folder the package contents are extracted into.</param>
    /// <param name="ct">Token used to cancel the extraction.</param>
    /// <returns><c>true</c> if the package was hydrated from the cache; otherwise <c>false</c>.</returns>
    public async Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct)
    {
        var archivePath = ArchivePath(packageId, version);
        if (!File.Exists(archivePath))
            return false;
        try
        {
            var targetRoot = Path.GetFullPath(targetPackageFolder);
            Directory.CreateDirectory(targetRoot);
            await using var stream = File.OpenRead(archivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                var destPath = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
                if (!destPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // zip-slip guard
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await using var outStream = File.Create(destPath);
                await using var inStream = entry.Open();
                await inStream.CopyToAsync(outStream, ct);
            }
            _logger.LogDebug("Hydrated {Id} {Version} from filesystem cache", packageId, version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hydrate failed for {Id} {Version}; will fall back to feed", packageId, version);
            return false;
        }
    }

    /// <summary>
    /// Persists the contents of <paramref name="sourcePackageFolder"/> into the cache as a single
    /// zip archive. Writes to a unique temp file then atomically moves it into place, and no-ops
    /// when the archive already exists, so concurrent replicas racing to save are safe.
    /// </summary>
    /// <param name="packageId">The package id being cached.</param>
    /// <param name="version">The exact package version being cached.</param>
    /// <param name="sourcePackageFolder">The installed package folder whose contents are archived.</param>
    /// <param name="ct">Token used to cancel the archive write.</param>
    /// <returns>A task that completes once the package has been saved (or skipped).</returns>
    public async Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct)
    {
        if (!Directory.Exists(sourcePackageFolder)) return;
        var archivePath = ArchivePath(packageId, version);
        if (File.Exists(archivePath)) return; // already cached — many replicas race to save
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        var tempPath = $"{archivePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var fileStream = File.Create(tempPath))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var root = Path.GetFullPath(sourcePackageFolder);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(rel, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var src = File.OpenRead(file);
                    await src.CopyToAsync(entryStream, ct);
                }
            }
            try
            {
                File.Move(tempPath, archivePath, overwrite: false);
                _logger.LogInformation("Saved {Id} {Version} to filesystem cache", packageId, version);
            }
            catch (IOException)
            {
                // Another replica won the race and created the archive first; fine.
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private string ArchivePath(string packageId, string version) =>
        Path.Combine(_root, packageId.ToLowerInvariant(), $"{version}.zip");
}
