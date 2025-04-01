using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Containers;
using MeshWeaver.Articles;
using MeshWeaver.Hosting.AzureBlob;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test;

public class ArticlesBlobStorageTest(ITestOutputHelper output) : ArticlesTest(output)
{

    private readonly IContainer azuriteContainer = ContainerExtensions.Azurite();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Start containers
        await azuriteContainer.StartAsync();
        await UploadMarkdownFiles();

    }

    private async Task UploadMarkdownFiles()
    {
        var markdownPath = Path.Combine(GetAssemblyLocation(), "Markdown");
        var files = Directory.GetFiles(markdownPath, "*", SearchOption.AllDirectories);

        // Get blob service client
        var blobServiceClient = new BlobServiceClient(ContainerExtensions.AzuriteConnectionString);

        // Get or create container
        var containerClient = blobServiceClient.GetBlobContainerClient(StorageProviders.Articles);
        await containerClient.CreateIfNotExistsAsync();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var blobClient = containerClient.GetBlobClient(fileName);

            // Upload file content
            await using var fileStream = File.OpenRead(file);
            await blobClient.UploadAsync(fileStream, overwrite: true);
        }
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
            .AddAzureBlobArticles()
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
    public override Task NotFound()
    {
        return base.NotFound();
    }

}
