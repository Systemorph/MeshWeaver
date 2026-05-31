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

    public FileSystemNuGetPackageCache(string root, ILogger<FileSystemNuGetPackageCache> logger)
    {
        _root = Path.GetFullPath(root);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

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
