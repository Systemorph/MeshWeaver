using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Containers;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.AzureBlob;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

// Helper class to provide lazy Azure client factory
internal class LazyBlobServiceClientFactory(Func<string> connectionStringProvider)
    : IAzureClientFactory<BlobServiceClient>
{
    private BlobServiceClient? _client;

    public BlobServiceClient CreateClient(string name)
    {
        return _client ??= new BlobServiceClient(connectionStringProvider(), new BlobClientOptions(BlobClientOptions.ServiceVersion.V2021_12_02));
    }
}

public class ArticlesBlobStorageTest : ArticlesTest
{
    private readonly IContainer azuriteContainer;
    private readonly string azuriteConnectionString;

    public ArticlesBlobStorageTest(ITestOutputHelper output) : base(output)
    {
        var (container, connectionString) = ContainerExtensions.CreateAzurite();
        azuriteContainer = container;
        azuriteConnectionString = connectionString;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureHub(mesh => mesh.WithServices(services =>
                services.AddSingleton<IAzureClientFactory<BlobServiceClient>>(serviceProvider =>
            {
                return new LazyBlobServiceClientFactory(() => azuriteConnectionString);
            }))
            );
    }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Start containers
        await azuriteContainer.StartAsync();

        // Wait for Azurite to be fully ready
        await Task.Delay(2000);

        await UploadMarkdownFiles();

    }

    private async Task UploadMarkdownFiles()
    {
        var markdownPath = Path.Combine(GetAssemblyLocation(), "Markdown");
        var files = Directory.GetFiles(markdownPath, "*", SearchOption.AllDirectories);

        // Get blob service client with compatible API version
        var blobClientOptions = new BlobClientOptions(BlobClientOptions.ServiceVersion.V2021_12_02);
        var blobServiceClient = new BlobServiceClient(azuriteConnectionString, blobClientOptions);

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

    public override async ValueTask DisposeAsync()
    {
        // Cleanup containers
        if (azuriteContainer is not null)
        {
            await azuriteContainer.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override MessageHubConfiguration ConfigureContentCollections(MessageHubConfiguration hub)
    {
        return hub
            .AddAzureBlob()
            .AddArticles(new ContentCollectionConfig()
            {
                Name = "Test",
                BasePath = StorageProviders.Articles,
                SourceType = MeshWeaver.Hosting.AzureBlob.AzureBlobStreamProviderFactory.SourceType,
                Settings = new Dictionary<string, string>
                {
                    ["ContainerName"] = StorageProviders.Articles,
                    ["ClientName"] = "default"
                }
            });
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
