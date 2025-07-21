using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using Xunit;
using MarkdownExtensions = MeshWeaver.ContentCollections.MarkdownExtensions;

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
            var markdownElement = MarkdownExtensions.ParseContent("demo", path, DateTime.UtcNow, content, ImmutableDictionary<string,Author>.Empty);
            var article = markdownElement.Should().BeOfType<Article>().Subject;
            
            article.Title.Should().Be("Northwind Overview");
            article.Abstract.Should().Be("This is a sample description of the article.");
            article.Name.Should().Be("Overview");
            article.Url.Should().Be("content/demo/Overview");
        }
    }
}
