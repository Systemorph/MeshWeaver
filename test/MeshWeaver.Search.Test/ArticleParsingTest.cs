using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
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
            var content = await File.ReadAllTextAsync(file, Encoding.Latin1);
            var (article, html) = MarkdownIndexer.ParseArticle(file, content, "demo");
            article.Should().NotBeNull();
            article.Name.Should().Be("Northwind Overview");
            article.Description.Should().Be("This is a sample description of the article.");
            article.Id.Should().Be("Overview");
            article.Extension.Should().Be(".md");
            article.Url.Should().Be("demo/Overview");
        }
    }
}
