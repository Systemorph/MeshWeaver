using System.Reactive.Linq;
using Azure;
using Azure.Storage.Blobs;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AzureBlob;

/// <summary>
/// Azure Blob-backed <see cref="IAssemblyStore"/>. Each <c>(nodeTypePath, version)</c>
/// pair is one blob; downloads are materialised into a process-local cache directory so
/// the runtime can load by filesystem path (<c>AssemblyLoadContext.LoadFromAssemblyPath</c>).
///
/// Cross-replica consistency: the cache key names exactly what it is (version of the
/// NodeType MeshNode), so two replicas compiling the same version produce bytes they
/// both agree correspond to that version. Concurrent <see cref="Put"/> calls overwrite
/// the same blob idempotently.
///
/// Security: write access to the backing container must be restricted to the service
/// principal — loading an arbitrary assembly is RCE-equivalent.
///
/// Wiring mirrors the content-collection blob pattern: the caller passes a
/// <see cref="BlobServiceClient"/> obtained via Aspire's keyed registration
/// (<c>AddKeyedAzureBlobServiceClient("nodetype-cache")</c>) plus the container name.
/// </summary>
public sealed class BlobAssemblyStore : IAssemblyStore
{
    /// <summary>
    /// Default Azure Blob container name for the assembly cache. Matches the Aspire
    /// resource name in <c>Memex.AppHost/Program.cs</c> so the same string is both the
    /// container and the keyed-service name.
    /// </summary>
    public const string DefaultContainerName = "nodetype-cache";

    private readonly BlobContainerClient container;
    private readonly string localCacheDirectory;
    private readonly ILogger<BlobAssemblyStore> logger;
    // Blob I/O leaves (download/upload) are bridged to IObservable through this
    // pool — never via a bare Observable.FromAsync, which only moves the subscribe
    // onto the pool and leaves the await continuation free to resume on a captured
    // scheduler and deadlock under a blocking subscriber. See IoPoolExtensions and
    // Doc/Architecture/AsynchronousCalls.md.
    private readonly IIoPool _ioPool;

    /// <summary>
    /// Initializes a new instance of the <c>BlobAssemblyStore</c> class.
    /// </summary>
    /// <param name="blobService">Azure Blob service client used to access the assembly-cache container.</param>
    /// <param name="containerName">Name of the blob container that stores compiled assemblies.</param>
    /// <param name="localCacheDirectory">Process-local directory into which downloaded assemblies are materialised for loading by path.</param>
    /// <param name="logger">Logger for store operations.</param>
    /// <param name="ioPoolRegistry">Optional I/O pool registry; when omitted, an unbounded pool is used for blob I/O.</param>
    public BlobAssemblyStore(
        BlobServiceClient blobService,
        string containerName,
        string localCacheDirectory,
        ILogger<BlobAssemblyStore> logger,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        this.container = blobService.GetBlobContainerClient(containerName);
        this.localCacheDirectory = localCacheDirectory;
        this.logger = logger;
        // Blob pool cap governs blob-storage concurrency; Unbounded is the DI-less
        // fallback (still offloads to the ThreadPool with ConfigureAwait).
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.Blob) ?? IoPool.Unbounded;
        Directory.CreateDirectory(localCacheDirectory);
    }

    /// <inheritdoc />
    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
    {
        var localPath = LocalPath(nodeTypePath, version);
        if (File.Exists(localPath))
        {
            // Already materialised in this process's local cache — no remote call needed.
            return Observable.Return<string?>(localPath);
        }
        return _ioPool.Run(async ct =>
        {
            await EnsureContainerAsync(ct).ConfigureAwait(false);
            var dllBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".dll"));
            var pdbBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".pdb"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await dllBlob.DownloadToAsync(localPath, ct).ConfigureAwait(false);
                try { await pdbBlob.DownloadToAsync(Path.ChangeExtension(localPath, ".pdb"), ct).ConfigureAwait(false); }
                catch (RequestFailedException rfe) when (rfe.Status == 404) { /* pdb optional */ }
                logger.LogInformation(
                    "Hydrated {NodeTypePath}@v{Version} from blob to {LocalPath}",
                    nodeTypePath, version, localPath);
                return (string?)localPath;
            }
            catch (RequestFailedException rfe) when (rfe.Status == 404)
            {
                return null;
            }
        });
    }

    /// <inheritdoc />
    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        => PutWithLocation(nodeTypePath, version, assemblyBytes, pdbBytes)
            .Select(loc => loc.LocalPath);

    /// <inheritdoc />
    public IObservable<AssemblyStoreLocation> PutWithLocation(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        var localPath = LocalPath(nodeTypePath, version);
        var dllBlobName = BlobName(nodeTypePath, version, ".dll");
        return _ioPool.Run(async ct =>
        {
            await EnsureContainerAsync(ct).ConfigureAwait(false);

            // Write to local cache first so the caller can load immediately without waiting
            // on the upload round-trip.
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, assemblyBytes, ct).ConfigureAwait(false);
            if (pdbBytes is { Length: > 0 })
                await File.WriteAllBytesAsync(Path.ChangeExtension(localPath, ".pdb"), pdbBytes, ct).ConfigureAwait(false);

            // Then upload. Overwrite=true is safe because two replicas compiling the same
            // version produce (near-)identical bytes.
            var dllBlob = container.GetBlobClient(dllBlobName);
            using (var ms = new MemoryStream(assemblyBytes))
                await dllBlob.UploadAsync(ms, overwrite: true, ct).ConfigureAwait(false);

            if (pdbBytes is { Length: > 0 })
            {
                var pdbBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".pdb"));
                using var ms = new MemoryStream(pdbBytes);
                await pdbBlob.UploadAsync(ms, overwrite: true, ct).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Uploaded {NodeTypePath}@v{Version} ({Bytes} bytes) to {Container}",
                nodeTypePath, version, assemblyBytes.Length, container.Name);
            return new AssemblyStoreLocation(localPath, container.Name, dllBlobName);
        });
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        // Exists-then-Create instead of CreateIfNotExistsAsync to skip the
        // Azure SDK's per-response "409 ContainerAlreadyExists" warning on
        // every startup against a pre-existing container.
        if (!await container.ExistsAsync(ct).ConfigureAwait(false))
            await container.CreateAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private string LocalPath(string nodeTypePath, long version) =>
        Path.Combine(localCacheDirectory, Sanitize(nodeTypePath), $"v{version}.dll");

    private static string BlobName(string nodeTypePath, long version, string extension) =>
        $"{nodeTypePath.TrimStart('/')}/v{version}{extension}";

    private static string Sanitize(string nodeTypePath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(nodeTypePath.Length);
        foreach (var c in nodeTypePath)
        {
            if (c == '_') sb.Append("__");
            else if (c == '/') sb.Append('_');
            else if (invalid.Contains(c)) sb.Append('-');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
