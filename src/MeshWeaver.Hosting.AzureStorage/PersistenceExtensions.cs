using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.AzureStorage;

/// <summary>
/// Extension methods for configuring Azure Blob Storage persistence.
/// </summary>
public static class PersistenceExtensions
{
    /// <summary>
    /// Adds Azure Blob Storage persistence services.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="containerClient">The blob container client</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobPersistence(
        this IServiceCollection services,
        BlobContainerClient containerClient)
    {
        var storageAdapter = new AzureBlobStorageAdapter(containerClient);
        var persistenceService = new InMemoryPersistenceService(storageAdapter);

        // Initialize the persistence service
        persistenceService.InitializeAsync().GetAwaiter().GetResult();

        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton<IPersistenceService>(persistenceService);

        return services;
    }

    /// <summary>
    /// Adds Azure Blob Storage persistence services with connection string.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="connectionString">Azure Storage connection string</param>
    /// <param name="containerName">The blob container name</param>
    /// <param name="createIfNotExists">Whether to create the container if it doesn't exist</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAzureBlobPersistence(
        this IServiceCollection services,
        string connectionString,
        string containerName,
        bool createIfNotExists = true)
    {
        var blobServiceClient = new BlobServiceClient(connectionString);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        if (createIfNotExists)
        {
            containerClient.CreateIfNotExists();
        }

        return services.AddAzureBlobPersistence(containerClient);
    }

    /// <summary>
    /// Adds Azure Blob Storage persistence services with async initialization.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="containerClient">The blob container client</param>
    /// <returns>The service collection for chaining</returns>
    public static async Task<IServiceCollection> AddAzureBlobPersistenceAsync(
        this IServiceCollection services,
        BlobContainerClient containerClient)
    {
        await containerClient.CreateIfNotExistsAsync();

        var storageAdapter = new AzureBlobStorageAdapter(containerClient);
        var persistenceService = new InMemoryPersistenceService(storageAdapter);

        await persistenceService.InitializeAsync();

        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton<IPersistenceService>(persistenceService);

        return services;
    }
}
