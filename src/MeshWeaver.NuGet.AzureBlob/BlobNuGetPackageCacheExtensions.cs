using Azure.Storage.Blobs;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.NuGet.AzureBlob;

/// <summary>
/// Service-collection extensions for registering the Azure Blob-backed NuGet package cache.
/// </summary>
public static class BlobNuGetPackageCacheExtensions
{
    /// <summary>
    /// Registers <see cref="BlobNuGetPackageCache"/> as the <see cref="INuGetPackageCache"/> backend.
    /// Expects a <see cref="BlobServiceClient"/> to already be registered in DI (e.g. via Aspire's
    /// AddAzureBlobClient extension).
    /// </summary>
    public static IServiceCollection AddBlobNuGetPackageCache(this IServiceCollection services, string containerName = "nuget-cache")
    {
        services.Replace(ServiceDescriptor.Singleton<INuGetPackageCache>(sp =>
            new BlobNuGetPackageCache(
                sp.GetRequiredService<BlobServiceClient>(),
                containerName,
                sp.GetRequiredService<ILogger<BlobNuGetPackageCache>>(),
                // Blob pool (mesh-scoped) governs blob concurrency; absent it falls back to
                // IoPool.Unbounded — still offloads, just uncapped. See ControlledIoPooling.md.
                sp.GetService<IoPoolRegistry>())));
        return services;
    }
}
