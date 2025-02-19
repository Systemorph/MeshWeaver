using System.Collections.Generic;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using MeshWeaver.Articles;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ArticlesInStorageTest(ITestOutputHelper output) : ArticlesTest(output)
{

    private readonly IContainer azuriteContainer = ContainerExtensions.Azurite();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Start containers
        await azuriteContainer.StartAsync();
    }

    public override async Task DisposeAsync()
    {
        // Cleanup containers
        if (azuriteContainer is not null)
        {
            await azuriteContainer.DisposeAsync();
        }

        await base.DisposeAsync();
    }


    protected override IServiceCollection ConfigureArticles(IServiceCollection services)
    {
        services.AddAzureClients(clientBuilder =>
        {
            clientBuilder.AddBlobServiceClient(ContainerExtensions.AzuriteConnectionString).WithName(StorageProviders.Articles); 
        });
        return services
            .AddAzureBlobArticleSource()
            .Configure<List<ArticleSourceConfig>>(
                options => options.Add(new ArticleSourceConfig()
                {
                    Name = "Test",
                    BasePath = StorageProviders.Articles,
                    SourceType = AzureBlobArticleCollectionFactory.SourceType,
                })
            );
    }
    [Fact]
    public override Task BasicArticle()
    {
        return base.BasicArticle();
    }

    [Fact]
    public override Task CalculatorThroughArticle()
    {
        return base.CalculatorThroughArticle();
    }

    [Fact]
    public override Task NotFound()
    {
        return base.NotFound();
    }

}
