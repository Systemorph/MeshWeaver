using System.IO.Compression;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeshWeaver.Mesh.Threading;
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
    // Blob I/O leaves (download/upload/exists/create) are bridged to IObservable
    // through this pool — never via a bare Observable.FromAsync, which only moves the
    // subscribe onto the pool and leaves the await continuation free to resume on a
    // captured scheduler and deadlock under a blocking subscriber. See IoPoolExtensions
    // and Doc/Architecture/ControlledIoPooling.md.
    private readonly IIoPool _ioPool;

    // Promise-cache for the "create the container exactly once" handshake. The first
    // caller kicks the Exists/Create round-trip off on the Blob pool; every later caller
    // composes off the SAME cached, ReplaySubject-backed observable (pool.Run) and
    // replays its completion. This replaces the forbidden SemaphoreSlim async gate:
    // there is no WaitAsync to park the action-block thread, and the init body still
    // runs at most once regardless of how many concurrent callers race here.
    // Lock guards only the reference assignment (synchronous, never blocking on I/O).
    private readonly object _initGate = new();
    private IObservable<Unit>? _containerReady;

    /// <summary>
    /// Creates a blob-backed NuGet package cache bound to the given container.
    /// </summary>
    /// <param name="blobService">The Azure Blob service client used to resolve the backing container.</param>
    /// <param name="containerName">Name of the blob container in which package archives are stored.</param>
    /// <param name="logger">Logger for hydrate/save diagnostics.</param>
    /// <param name="ioPoolRegistry">
    /// Optional registry providing the Blob I/O pool that bounds blob-storage concurrency;
    /// when absent, an unbounded fallback pool is used.
    /// </param>
    public BlobNuGetPackageCache(
        BlobServiceClient blobService,
        string containerName,
        ILogger<BlobNuGetPackageCache> logger,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _container = blobService.GetBlobContainerClient(containerName);
        _logger = logger;
        // Blob pool cap governs blob-storage concurrency; Unbounded is the DI-less
        // fallback (still offloads to the ThreadPool with ConfigureAwait).
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.Blob) ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Attempts to restore a cached package version into the target folder by downloading and
    /// extracting its blob archive. Returns <c>false</c> when the package is not cached or the
    /// restore fails (the caller then falls back to the feed).
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The package version to hydrate.</param>
    /// <param name="targetPackageFolder">Destination folder to extract the package contents into.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns><c>true</c> if the package was found and extracted; otherwise <c>false</c>.</returns>
    public async Task<bool> TryHydrateAsync(string packageId, string version, string targetPackageFolder, CancellationToken ct)
    {
        await EnsureContainerReady(ct);
        var blob = _container.GetBlobClient(BlobName(packageId, version));
        try
        {
            return await _ioPool.Run(async poolCt =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, poolCt);
                await using var stream = await blob.OpenReadAsync(cancellationToken: linked.Token).ConfigureAwait(false);
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
                    await inStream.CopyToAsync(outStream, linked.Token).ConfigureAwait(false);
                }
                _logger.LogDebug("Hydrated {Id} {Version} from blob", packageId, version);
                return true;
            }).FirstAsync().ToTask(ct).ConfigureAwait(false);
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

    /// <summary>
    /// Zips the contents of the source package folder and uploads it as the cached blob for the
    /// given package version. Skips the upload when the blob already exists (another replica won
    /// the race) and is a no-op when the source folder is missing.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The package version to save.</param>
    /// <param name="sourcePackageFolder">Folder whose contents are zipped and uploaded.</param>
    /// <param name="ct">Token to cancel the operation.</param>
    /// <returns>A task that completes when the save attempt finishes.</returns>
    public async Task SaveAsync(string packageId, string version, string sourcePackageFolder, CancellationToken ct)
    {
        if (!Directory.Exists(sourcePackageFolder)) return;
        await EnsureContainerReady(ct);

        await _ioPool.Run<Unit>(async poolCt =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, poolCt);
            var lct = linked.Token;
            var blob = _container.GetBlobClient(BlobName(packageId, version));
            // Skip if already saved — many replicas will try to save the same package concurrently.
            if (await blob.ExistsAsync(lct).ConfigureAwait(false)) return Unit.Default;

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
                    await fileStream.CopyToAsync(entryStream, lct).ConfigureAwait(false);
                }
            }
            buffer.Position = 0;
            try
            {
                await blob.UploadAsync(buffer, overwrite: false, lct).ConfigureAwait(false);
                _logger.LogInformation("Saved {Id} {Version} to blob ({Size} bytes)", packageId, version, buffer.Length);
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Another replica won the race; fine.
            }
            return Unit.Default;
        }).FirstAsync().ToTask(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the backing container exists, running the Exists/Create round-trip at most
    /// once across all concurrent callers via the IoPool promise-cache (replacing the
    /// former SemaphoreSlim async gate). The cached observable is built under a plain
    /// synchronous lock that never wraps an await, so the action-block thread is never
    /// parked; the actual blob round-trip runs inside <see cref="_ioPool"/>.
    /// </summary>
    private Task EnsureContainerReady(CancellationToken ct)
    {
        var ready = _containerReady;
        if (ready is null)
        {
            lock (_initGate)
            {
                ready = _containerReady ??= _ioPool.Run(InitializeContainerAsync);
            }
        }
        // Compose off the shared, replayed completion. ToTask bridges to the external
        // Task contract; the work itself already ran (once) on the pool.
        return ready.FirstAsync().ToTask(ct);
    }

    private async Task<Unit> InitializeContainerAsync(CancellationToken ct)
    {
        // Exists-then-Create instead of CreateIfNotExistsAsync to skip the
        // Azure SDK's per-response "409 ContainerAlreadyExists" warning on
        // every startup against the pre-existing nuget-cache container.
        if (!await _container.ExistsAsync(cancellationToken: ct).ConfigureAwait(false))
            await _container.CreateAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
        return Unit.Default;
    }

    private static string BlobName(string packageId, string version) =>
        $"{packageId.ToLowerInvariant()}/{version}.zip";
}
