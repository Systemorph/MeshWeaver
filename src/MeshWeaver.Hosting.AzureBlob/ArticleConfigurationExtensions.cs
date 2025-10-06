// In an appropriate extensions class (e.g., ArticleConfigurationExtensions.cs)

using Azure.Storage.Blobs;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.AzureBlob;

public static class ArticleConfigurationExtensions
{
    public static IServiceCollection AddAzureBlobArticles(
        this IServiceCollection services)
    {
        return services
            .AddKeyedSingleton<IContentCollectionFactory, AzureBlobContentCollectionFactory>(
                AzureBlobContentCollectionFactory.SourceType);
    }

    public static IServiceCollection AddAzureBlobStreamProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var config = configuration.GetSection("StreamProviders").Get<StreamProvidersConfiguration>();
        if (config?.Providers == null)
        {
            return services;
        }

        foreach (var providerConfig in config.Providers.Where(p => p.ProviderType == "AzureBlob"))
        {
            services.AddKeyedSingleton<IStreamProvider>(providerConfig.Name, (sp, key) =>
            {
                var factory = sp.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();

                // Try to get client name from settings, default to "default"
                var clientName = providerConfig.Settings.GetValueOrDefault("ClientName", "default");
                var containerName = providerConfig.Settings.GetValueOrDefault("ContainerName", providerConfig.Name);
                var blobServiceClient = factory.CreateClient(clientName);

                return new AzureBlobStreamProvider(blobServiceClient, containerName);
            });
        }

        return services;
    }
}

public class AzureBlobContentCollectionFactory(IServiceProvider serviceProvider) : IContentCollectionFactory
{
    public const string SourceType = "AzureBlob";

    public ContentCollection Create(ContentSourceConfig config, IMessageHub hub)
    {
        var factory = serviceProvider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();
        var blobServiceClient = factory.CreateClient(config.BasePath);

        // Container name should be in config or use collection name
        var containerName = config.Name!;
        var provider = new AzureBlobStreamProvider(blobServiceClient, containerName);

        return new ContentCollection(config, provider, hub);
    }
}
