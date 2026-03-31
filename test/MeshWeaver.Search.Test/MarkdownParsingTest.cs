using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Parsers;
using Xunit;

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

            var markdownElement = ContentCollectionsExtensions.ParseContent("demo", path, DateTime.UtcNow, content, null);

            markdownElement.Should().NotBeNull();
            markdownElement.Should().BeOfType<MarkdownElement>();
            markdownElement.Name.Should().Be("Overview");
            markdownElement.Url.Should().Be("/content/demo/Overview");
            markdownElement.Content.Should().Contain("# Northwind");
            markdownElement.PrerenderedHtml.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ParseContent_WithYaml_ReturnsMarkdownElement()
    {
        var markdown = """
            ---
            Title: "My Article"
            Abstract: "A short summary"
            Thumbnail: "images/thumb.png"
            ---

            # Hello World

            Some content here.
            """;

        var result = ContentCollectionsExtensions.ParseContent("docs", "my-article.md", DateTime.UtcNow, markdown, null);

        result.Should().BeOfType<MarkdownElement>();
        result.Name.Should().Be("my-article");
        result.Collection.Should().Be("docs");
        result.Url.Should().Be("/content/docs/my-article");
        result.Content.Should().Contain("# Hello World");
    }

    [Fact]
    public void ParseContent_WithoutYaml_ReturnsMarkdownElement()
    {
        var markdown = """
            # Plain Markdown

            No front matter here.
            """;

        var result = ContentCollectionsExtensions.ParseContent("docs", "plain.md", DateTime.UtcNow, markdown, null);

        result.Should().BeOfType<MarkdownElement>();
        result.Name.Should().Be("plain");
        result.Content.Should().Contain("# Plain Markdown");
    }

    [Fact]
    public void ParseContent_WithMalformedYaml_ReturnsMarkdownElement()
    {
        var markdown = """
            ---
            Title: [invalid yaml
            : broken: :::
            ---

            # Content after bad YAML
            """;

        var result = ContentCollectionsExtensions.ParseContent("docs", "broken.md", DateTime.UtcNow, markdown, null);

        result.Should().BeOfType<MarkdownElement>();
        result.Name.Should().Be("broken");
        result.Content.Should().Contain("# Content after bad YAML");
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
            node.NodeType.Should().Be("Markdown");
            node.Content.Should().BeOfType<MeshWeaver.Markdown.MarkdownContent>();
            ((MeshWeaver.Markdown.MarkdownContent)node.Content!).Content.Should().Contain("# Northwind");
        }
    }
}
