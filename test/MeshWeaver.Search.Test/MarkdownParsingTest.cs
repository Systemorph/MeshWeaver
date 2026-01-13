using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Parsers;

namespace MeshWeaver.Search.Test;

/// <summary>
/// Tests for markdown parsing functionality.
/// </summary>
public class MarkdownParsingTest(ITestOutputHelper output) : HubTestBase(output)
{
    [HubFact]
    public async Task ParseContent_ReturnsMarkdownElement()
    {
        var assemblyLoc = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var baseDir = Path.Combine(assemblyLoc, "wwwroot");
        var files = Directory.EnumerateFiles(baseDir, "*.md");

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var path = Path.GetRelativePath(baseDir, file);

            // ParseContent now returns MarkdownElement (not Article)
            var markdownElement = ContentCollectionsExtensions.ParseContent("demo", path, DateTime.UtcNow, content, null);

            markdownElement.Should().NotBeNull();
            markdownElement.Name.Should().Be("Overview");
            markdownElement.Url.Should().Be("/content/demo/Overview");
            markdownElement.Content.Should().Contain("# Northwind");
            markdownElement.PrerenderedHtml.Should().NotBeNullOrEmpty();
        }
    }

    [HubFact]
    public async Task MarkdownFileParser_ParsesNewFormat()
    {
        var assemblyLoc = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var baseDir = Path.Combine(assemblyLoc, "wwwroot");
        var files = Directory.EnumerateFiles(baseDir, "*.md");

        var parser = new MarkdownFileParser();

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            var relativePath = Path.GetRelativePath(baseDir, file);

            var node = await parser.ParseAsync(file, content, relativePath);

            node.Should().NotBeNull();
            node!.Id.Should().Be("Overview");
            node.Name.Should().Be("Northwind Overview");
            node.Category.Should().Be("Documentation");
            node.Description.Should().Be("This is a sample description of the article.");
            node.NodeType.Should().Be("Markdown");
            node.Content.Should().BeOfType<string>();
            ((string)node.Content!).Should().Contain("# Northwind");
        }
    }
}
