using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for interactive markdown functionality using MeshNode and MarkdownFileParser.
/// </summary>
public class InteractiveMarkdownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<IFileFormatParser, MarkdownFileParser>());

    protected string GetMarkdownDirectory()
    {
        var location = GetType().Assembly.Location;
        return Path.Combine(Path.GetDirectoryName(location)!, "Markdown");
    }

    [Fact]
    public async Task MarkdownFileParser_ParsesYamlFrontMatter()
    {
        // Arrange
        var parser = new MarkdownFileParser();
        var markdownPath = Path.Combine(GetMarkdownDirectory(), "Overview.md");
        var content = await File.ReadAllTextAsync(markdownPath);

        // Act
        var node = await parser.ParseAsync(markdownPath, content, "Test/Overview.md");

        // Assert
        node.Should().NotBeNull();
        node!.Id.Should().Be("Overview");
        node.Namespace.Should().Be("Test");
        node.Name.Should().Be("Northwind Overview");
        node.Category.Should().Be("Documentation");
        node.NodeType.Should().Be("Markdown");
        node.Content.Should().BeOfType<MarkdownContent>();
        ((MarkdownContent)node.Content!).Content.Should().Contain("# Northwind");
    }

    [Fact]
    public async Task MarkdownFileParser_HandlesNoYamlFrontMatter()
    {
        // Arrange
        var parser = new MarkdownFileParser();
        var content = "# Simple Markdown\n\nNo YAML front matter here.";
        var tempPath = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(tempPath, content);

            // Act
            var node = await parser.ParseAsync(tempPath, content, "Test/Simple.md");

            // Assert
            node.Should().NotBeNull();
            node!.Id.Should().Be("Simple");
            node.Name.Should().Be("Simple"); // Defaults to Id
            node.NodeType.Should().Be("Markdown");
            node.Content.Should().BeOfType<MarkdownContent>();
            ((MarkdownContent)node.Content!).Content.Should().Be(content);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task MarkdownFileParser_SerializesNodeToMarkdown()
    {
        // Arrange
        var parser = new MarkdownFileParser();
        var node = new MeshNode("TestDoc", "Docs")
        {
            Name = "Test Document",
            Category = "Testing",
            NodeType = "Markdown",
            Content = "# Hello World\n\nThis is content."
        };

        // Act
        var markdown = await parser.SerializeAsync(node);

        // Assert
        markdown.Should().Contain("---");
        markdown.Should().Contain("Name: Test Document");
        markdown.Should().Contain("Category: Testing");
        markdown.Should().Contain("# Hello World");
    }

    [Fact]
    public void MarkdownFileParser_CanSerializeMarkdownNodes()
    {
        // Arrange
        var parser = new MarkdownFileParser();
        var markdownNode = new MeshNode("doc", "test") { NodeType = "Markdown" };
        var stringContentNode = new MeshNode("doc", "test") { Content = "some string" };
        var otherNode = new MeshNode("doc", "test") { NodeType = "Other", Content = 123 };

        // Act & Assert
        parser.CanSerialize(markdownNode).Should().BeTrue();
        parser.CanSerialize(stringContentNode).Should().BeTrue();
        parser.CanSerialize(otherNode).Should().BeFalse();
    }
}
