using System.Reactive.Linq;
using Azure;
using Azure.Storage.Blobs;
using MeshWeaver.Mesh.Services;
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

    public BlobAssemblyStore(
        BlobServiceClient blobService,
        string containerName,
        string localCacheDirectory,
        ILogger<BlobAssemblyStore> logger)
    {
        this.container = blobService.GetBlobContainerClient(containerName);
        this.localCacheDirectory = localCacheDirectory;
        this.logger = logger;
        Directory.CreateDirectory(localCacheDirectory);
    }

    public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version)
    {
        var localPath = LocalPath(nodeTypePath, version);
        if (File.Exists(localPath))
        {
            // Already materialised in this process's local cache — no remote call needed.
            return Observable.Return<string?>(localPath);
        }
        return Observable.FromAsync(async () =>
        {
            await EnsureContainerAsync();
            var dllBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".dll"));
            var pdbBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".pdb"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await dllBlob.DownloadToAsync(localPath);
                try { await pdbBlob.DownloadToAsync(Path.ChangeExtension(localPath, ".pdb")); }
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

    public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
    {
        var localPath = LocalPath(nodeTypePath, version);
        return Observable.FromAsync(async () =>
        {
            await EnsureContainerAsync();

            // Write to local cache first so the caller can load immediately without waiting
            // on the upload round-trip.
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, assemblyBytes);
            if (pdbBytes is { Length: > 0 })
                await File.WriteAllBytesAsync(Path.ChangeExtension(localPath, ".pdb"), pdbBytes);

            // Then upload. Overwrite=true is safe because two replicas compiling the same
            // version produce (near-)identical bytes.
            var dllBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".dll"));
            using (var ms = new MemoryStream(assemblyBytes))
                await dllBlob.UploadAsync(ms, overwrite: true);

            if (pdbBytes is { Length: > 0 })
            {
                var pdbBlob = container.GetBlobClient(BlobName(nodeTypePath, version, ".pdb"));
                using var ms = new MemoryStream(pdbBytes);
                await pdbBlob.UploadAsync(ms, overwrite: true);
            }

            logger.LogInformation(
                "Uploaded {NodeTypePath}@v{Version} ({Bytes} bytes) to {Container}",
                nodeTypePath, version, assemblyBytes.Length, container.Name);
            return localPath;
        });
    }

    private async Task EnsureContainerAsync()
    {
        try { await container.CreateIfNotExistsAsync(); }
        catch (RequestFailedException rfe) when (rfe.Status == 409) { /* already exists */ }
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
