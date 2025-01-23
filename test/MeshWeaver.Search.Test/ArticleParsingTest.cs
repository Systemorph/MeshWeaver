using System.IO;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using FluentAssertions;
using MeshWeaver.Articles;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
using Xunit.Abstractions;

namespace MeshWeaver.Search.Test;

public class ArticleParsingTest(ITestOutputHelper output) : HubTestBase(output)
{
    [HubFact]
    public async Task TestIndexing()
    {
        var assemblyLoc = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var baseDir = Path.Combine(assemblyLoc, "wwwroot");
        var files = Directory.EnumerateFiles(baseDir);

        var connectionString = "UseDevelopmentStorage=true";
        var blobServiceClient = new BlobServiceClient(connectionString);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var path = Path.GetRelativePath(baseDir, file);
            var article = ArticleExtensions.ParseArticle("demo", path, content);
            article.Should().NotBeNull();
            article.Title.Should().Be("Northwind Overview");
            article.Abstract.Should().Be("This is a sample description of the article.");
            article.Name.Should().Be("Overview");
            article.Extension.Should().Be(".md");
            article.Url.Should().Be("article/demo/Overview.md");
        }
    }
}
