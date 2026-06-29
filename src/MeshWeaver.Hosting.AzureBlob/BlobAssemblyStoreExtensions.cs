using Azure.Storage.Blobs;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.AzureBlob;

/// <summary>
/// DI helpers for the Azure Blob-backed <see cref="IAssemblyStore"/>. Wiring mirrors
/// <c>BlobNuGetPackageCache</c>: the caller registers a keyed
/// <see cref="BlobServiceClient"/> under the container name
/// (typically via Aspire's <c>AddKeyedAzureBlobServiceClient("nodetype-cache")</c>),
/// then registers the store pointed at that keyed client.
/// </summary>
public static class BlobAssemblyStoreExtensions
{
    /// <summary>
    /// Registers <see cref="BlobAssemblyStore"/> as the <see cref="IAssemblyStore"/>
    /// singleton, backed by a keyed <see cref="BlobServiceClient"/> registered under
    /// <paramref name="clientKeyAndContainer"/>. The service key and the container name
    /// are the same string on purpose — the Aspire resource name is both.
    /// </summary>
    public static IServiceCollection AddBlobAssemblyStore(
        this IServiceCollection services,
        string clientKeyAndContainer = BlobAssemblyStore.DefaultContainerName,
        string? localCacheDirectory = null)
    {
        var cacheDir = localCacheDirectory
            ?? Path.Combine(Path.GetTempPath(), "meshweaver-assembly-cache");
        services.TryAddSingleton<IAssemblyStore>(sp => new BlobAssemblyStore(
            sp.GetRequiredKeyedService<BlobServiceClient>(clientKeyAndContainer),
            clientKeyAndContainer,
            cacheDir,
            sp.GetRequiredService<ILogger<BlobAssemblyStore>>()));
        return services;
    }
}
