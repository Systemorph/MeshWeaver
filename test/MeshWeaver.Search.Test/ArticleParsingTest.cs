using System.Text;
using Azure.Provisioning.Storage;
using Azure.Storage.Blobs;
using MeshWeaver.Hub.Fixture;
using Xunit.Abstractions;

namespace MeshWeaver.Search.Test;

public class ArticleParsingTest(ITestOutputHelper output) : HubTestBase(output)
{
    [HubFact]
    public async Task TestIndexing()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), "wwwroot"));

        var connectionString = "UseDevelopmentStorage=true";
        var blobServiceClient = new BlobServiceClient(connectionString);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var (article, html) = MarkdownIndexer.ParseArticle(file, content, "app/demo/dashboard");

            // Store properties of article as metadata
            //var metadata = new Dictionary<string, string>
            //{
            //    { "Title", article.Name },
            //    { "Author", article.Author },
            //    { "Date", article.Date.ToString() }
            //};

            // Store html as file content on blob client
            //var blobContainerClient = blobServiceClient.GetBlobContainerClient("articles");
            //var blobClient = blobContainerClient.GetBlobClient(article.Path);
            //await blobClient.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes(html)), metadata);
        }
    }
}
