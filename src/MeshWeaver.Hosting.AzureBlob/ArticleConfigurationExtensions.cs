// In an appropriate extensions class (e.g., ArticleConfigurationExtensions.cs)

using Azure.Storage.Blobs;
using MeshWeaver.Articles;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.AzureBlob;

public static class ArticleConfigurationExtensions
{
    public static IServiceCollection AddAzureBlobArticleSource(
        this IServiceCollection services)
    {
        return services
            .AddKeyedSingleton<IArticleCollectionFactory, AzureBlobArticleCollectionFactory>(
                AzureBlobArticleCollectionFactory.SourceType);
    }
}

public class AzureBlobArticleCollectionFactory(IMessageHub hub, IServiceProvider serviceProvider) : IArticleCollectionFactory
{
    public const string SourceType = "AzureBlob";

    public ArticleCollection Create(ArticleSourceConfig config)
    {

        var factory = serviceProvider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>();
        var blobServiceClient = factory.CreateClient(StorageProviders.Articles);

        return new AzureBlobArticleCollection(config, hub, blobServiceClient);
    }
}
