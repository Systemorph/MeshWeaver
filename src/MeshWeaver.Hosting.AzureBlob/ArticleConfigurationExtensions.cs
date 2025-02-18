// In an appropriate extensions class (e.g., ArticleConfigurationExtensions.cs)

using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Articles;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.AzureBlob;

public static class ArticleConfigurationExtensions
{
    public static MeshBuilder AddAzureBlobArticleSource(
        this MeshBuilder builder)
    {
        return builder.ConfigureServices(s => s
            .AddKeyedSingleton<IArticleCollectionFactory, AzureBlobArticleCollectionFactory>(
                AzureBlobArticleCollectionFactory.SourceType));
    }
}

public class AzureBlobArticleCollectionFactory(IMessageHub hub) : IArticleCollectionFactory
{
    public const string SourceType = "AzureBlob";

    public ArticleCollection Create(ArticleSourceConfig config)
    {
        return new AzureBlobArticleCollection(config, hub);
    }
}
