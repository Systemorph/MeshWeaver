// In an appropriate extensions class (e.g., ArticleConfigurationExtensions.cs)

using Azure.Storage.Blobs;
using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.AzureBlob;

public static class ArticleConfigurationExtensions
{
    public static IServiceCollection AddAzureBlobArticles(
        this IServiceCollection services)
    {
        return services
            .AddContentCollections()
            .AddKeyedSingleton<IContentCollectionFactory, AzureBlobContentCollectionFactory>(
                AzureBlobContentCollectionFactory.SourceType);
    }
}

public class AzureBlobContentCollectionFactory(IMessageHub hub, IServiceProvider serviceProvider) : IContentCollectionFactory
{
    public const string SourceType = "AzureBlob";

    public ContentCollection Create(ContentSourceConfig config)
    {

        var factory = serviceProvider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();
        var blobServiceClient = factory.CreateClient(config.BasePath);

        return new AzureBlobContentCollection(config, hub, blobServiceClient);
    }
}
