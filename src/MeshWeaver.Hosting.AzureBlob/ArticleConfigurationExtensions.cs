using Azure.Storage.Blobs;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.AzureBlob;

public static class ArticleConfigurationExtensions
{
    public static IServiceCollection AddAzureBlob(
        this IServiceCollection services)
    {
        // Register the stream provider factory for AzureBlob source type
        services.AddKeyedSingleton<IStreamProviderFactory, AzureBlobStreamProviderFactory>(
            AzureBlobStreamProviderFactory.SourceType);

        // Register fallback factory only if IAzureClientFactory is not already registered.
        // This allows Aspire's AddAzureBlobClient to take precedence when available,
        // while falling back to connection string configuration for non-Aspire scenarios.
        services.TryAddSingleton<IAzureClientFactory<BlobServiceClient>, FallbackBlobServiceClientFactory>();

        return services;
    }
    public static MessageHubConfiguration AddAzureBlob(
        this MessageHubConfiguration config) =>
        config.WithServices(AddAzureBlob);

    /// <summary>
    /// Adds an Azure Blob content collection to the message hub configuration
    /// </summary>
    /// <param name="configuration">The message hub configuration</param>
    /// <param name="collectionName">The name of the collection</param>
    /// <param name="containerName">The Azure Blob container name</param>
    /// <param name="clientName">The name of the Azure client (default: "default")</param>
    /// <returns>The configured message hub configuration</returns>
    public static MessageHubConfiguration AddAzureBlobContentCollection(
        this MessageHubConfiguration configuration,
        string collectionName,
        string containerName,
        string clientName = "default")
        => configuration
            .AddContentCollections()
            .WithServices(services =>
            {
                // Ensure the factory is registered
                services.AddKeyedScoped<IStreamProviderFactory, AzureBlobStreamProviderFactory>(
                    AzureBlobStreamProviderFactory.SourceType);

                // Register the content collection provider
                services.AddScoped<IContentCollectionConfigProvider>(_ =>
                {
                    var config = new ContentCollectionConfig
                    {
                        Name = collectionName,
                        SourceType = AzureBlobStreamProviderFactory.SourceType,
                        Settings = new Dictionary<string, string>
                        {
                            ["ContainerName"] = containerName,
                            ["ClientName"] = clientName
                        },
                        Address = configuration.Address
                    };
                    return new ContentCollectionConfigProvider(config);
                });

                return services;
            });
}

public class AzureBlobStreamProviderFactory(IServiceProvider serviceProvider) : IStreamProviderFactory
{
    public const string SourceType = "AzureBlob";

    public Task<IStreamProvider> CreateAsync(ContentCollectionConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Settings == null)
            throw new ArgumentException("Settings are required for AzureBlob source type");

        if (!config.Settings.TryGetValue("ContainerName", out var containerName))
            throw new ArgumentException("ContainerName is required in settings for AzureBlob source type");

        var factory = serviceProvider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();

        // Try to get client name from settings, default to "default"
        var clientName = config.Settings.GetValueOrDefault("ClientName", "default");
        var blobServiceClient = factory.CreateClient(clientName);

        var basePath = config.BasePath ?? config.Settings?.GetValueOrDefault("BasePath") ?? "";
        return Task.FromResult<IStreamProvider>(new AzureBlobStreamProvider(blobServiceClient, containerName, basePath));
    }
}

/// <summary>
/// Fallback factory that creates BlobServiceClient from connection string configuration.
/// Used when IAzureClientFactory is not registered (e.g., non-Aspire scenarios).
/// Reads connection string from Graph:ConnectionString configuration section.
/// </summary>
public class FallbackBlobServiceClientFactory(IConfiguration configuration) : IAzureClientFactory<BlobServiceClient>
{
    public BlobServiceClient CreateClient(string name)
    {
        var connectionString = configuration.GetSection("Graph")["ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Graph:ConnectionString is required for AzureBlob content collections when IAzureClientFactory<BlobServiceClient> is not registered. " +
                "Either configure the connection string in appsettings.json, or use Aspire's AddAzureBlobClient to register the Azure client factory.");
        }
        return new BlobServiceClient(connectionString);
    }
}
