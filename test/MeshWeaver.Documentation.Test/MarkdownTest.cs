using System.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Testing custom markdown extensions
/// </summary>
/// <param name="output"></param>
public class MarkdownTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TestAddress = "app/test";
    /// <summary>
    /// This tests the rendering of Layout Area Markdown
    /// </summary>
    [HubFact]
    public void BasicLayoutArea()
    {
        // Define a sample markdown string
        var markdown = $"@(\"{TestAddress}/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);
        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");

    }


    private static MarkdownDocument ParseMarkdown<TExtension>(string markdown, TExtension markdownExtension
    ) where TExtension : class, IMarkdownExtension
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();
        return Markdig.Markdown.Parse(markdown, pipeline);
    }

    private static string RenderMarkdown<TExtension>(string markdown, TExtension markdownExtension
    ) where TExtension : class, IMarkdownExtension
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();
        var html = Markdig.Markdown.ToHtml(markdown, pipeline);
        return html;
    }
    /// <summary>
    /// This tests the rendering of two Layout Area Markdown
    /// </summary>

    [HubFact]
    public void TwoLayoutAreas()
    {
        // Define a sample markdown string
        var markdown = "@(\"app/demo/Area1\")\n@(\"app/demo/Area2\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown( markdown, extension);
        var areas = document.Descendants<LayoutAreaComponentInfo>().ToArray();

        areas.Should().HaveCount(2);
        var area1 = areas[0];
        var area2 = areas[1];
        area1.Area.Should().Be("Area1");
        area2.Area.Should().Be("Area2");

    }


    /// <summary>
    ///
    /// </summary>
    [Fact]
    public void ParseCodeBlock()
    {
        var codeBlock =
            @"```csharp execute
Console.WriteLine(""Hello World"");
```";
        var extension = new ExecutableCodeBlockExtension();
        var html = RenderMarkdown(codeBlock, extension);
    }

    #region Unified Content Reference Tests

    [Fact]
    public void ParseDataContentReference()
    {
        var markdown = "@(\"data:app/test/MyCollection\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var dataBlocks = document.Descendants<DataContentBlock>().ToArray();
        dataBlocks.Should().HaveCount(1);

        var block = dataBlocks[0];
        block.DataReference.AddressType.Should().Be("app");
        block.DataReference.AddressId.Should().Be("test");
        block.DataReference.Collection.Should().Be("MyCollection");
        block.DataReference.EntityId.Should().BeNull();
    }

    [Fact]
    public void ParseDataContentReferenceWithEntityId()
    {
        var markdown = "@(\"data:app/test/MyCollection/entity123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var dataBlocks = document.Descendants<DataContentBlock>().ToArray();
        dataBlocks.Should().HaveCount(1);

        var block = dataBlocks[0];
        block.DataReference.Collection.Should().Be("MyCollection");
        block.DataReference.EntityId.Should().Be("entity123");
    }

    [Fact]
    public void ParseDataContentReferenceDefault()
    {
        var markdown = "@(\"data:app/test\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var dataBlocks = document.Descendants<DataContentBlock>().ToArray();
        dataBlocks.Should().HaveCount(1);

        var block = dataBlocks[0];
        block.DataReference.AddressType.Should().Be("app");
        block.DataReference.AddressId.Should().Be("test");
        block.DataReference.IsDefaultReference.Should().BeTrue();
    }

    [Fact]
    public void ParseFileContentReference()
    {
        var markdown = "@(\"content:app/test/Documents/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var fileBlocks = document.Descendants<FileContentBlock>().ToArray();
        fileBlocks.Should().HaveCount(1);

        var block = fileBlocks[0];
        block.FileReference.AddressType.Should().Be("app");
        block.FileReference.AddressId.Should().Be("test");
        block.FileReference.Collection.Should().Be("Documents");
        block.FileReference.FilePath.Should().Be("report.pdf");
        block.FileReference.Partition.Should().BeNull();
    }

    [Fact]
    public void ParseFileContentReferenceWithPartition()
    {
        var markdown = "@(\"content:app/test/Documents@2024/report.pdf\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var fileBlocks = document.Descendants<FileContentBlock>().ToArray();
        fileBlocks.Should().HaveCount(1);

        var block = fileBlocks[0];
        block.FileReference.Collection.Should().Be("Documents");
        block.FileReference.Partition.Should().Be("2024");
        block.FileReference.FilePath.Should().Be("report.pdf");
    }

    [Fact]
    public void ParseFileContentReferenceWithNestedPath()
    {
        var markdown = "@(\"content:app/test/Documents/folder/subfolder/file.txt\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var fileBlocks = document.Descendants<FileContentBlock>().ToArray();
        fileBlocks.Should().HaveCount(1);

        var block = fileBlocks[0];
        block.FileReference.FilePath.Should().Be("folder/subfolder/file.txt");
    }

    [Fact]
    public void ParseAreaContentReference()
    {
        var markdown = "@(\"area:app/test/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");
        area.Address.Should().Be("app/test");
    }

    [Fact]
    public void ParseAreaContentReferenceWithId()
    {
        var markdown = "@(\"area:app/test/MyArea/item123\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);

        var area = layoutAreas[0];
        area.Area.Should().Be("MyArea");
        area.Id.Should().Be("item123");
    }

    [Fact]
    public void LegacyFormatStillWorks()
    {
        // Ensure backward compatibility with existing format
        var markdown = "@(\"app/test/MyArea\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var layoutAreas = document.Descendants<LayoutAreaComponentInfo>().ToArray();
        layoutAreas.Should().HaveCount(1);
        layoutAreas[0].Area.Should().Be("MyArea");
    }

    [Fact]
    public void RenderDataContentBlock()
    {
        var markdown = "@(\"data:app/test/Users\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        html.Should().Contain("class='data-content'");
        html.Should().Contain("data-address='app/test'");
        html.Should().Contain("data-path='data:app/test/Users'");
    }

    [Fact]
    public void RenderFileContentBlock()
    {
        var markdown = "@(\"content:app/test/Docs/readme.md\")";
        var extension = new LayoutAreaMarkdownExtension();
        var html = RenderMarkdown(markdown, extension);

        html.Should().Contain("class='file-content'");
        html.Should().Contain("data-address='app/test'");
        html.Should().Contain("data-path='content:app/test/Docs/readme.md'");
    }

    [Fact]
    public void MixedReferenceTypes()
    {
        var markdown = @"
@(""data:app/test/Users"")
@(""content:app/test/Docs/file.txt"")
@(""area:app/test/Dashboard"")
";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        document.Descendants<DataContentBlock>().Should().HaveCount(1);
        document.Descendants<FileContentBlock>().Should().HaveCount(1);
        document.Descendants<LayoutAreaComponentInfo>().Should().HaveCount(1);
    }

    [Fact]
    public void DataContentBlockPath()
    {
        var markdown = "@(\"data:pricing/MS-2024/PropertyRisk/risk1\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var dataBlocks = document.Descendants<DataContentBlock>().ToArray();
        dataBlocks.Should().HaveCount(1);

        var block = dataBlocks[0];
        block.Path.Should().Be("data:pricing/MS-2024/PropertyRisk/risk1");
        block.Address.Should().Be("pricing/MS-2024");
    }

    [Fact]
    public void FileContentBlockPath()
    {
        var markdown = "@(\"content:host/1/Submissions@MS-2024/folder/file.xlsx\")";
        var extension = new LayoutAreaMarkdownExtension();
        var document = ParseMarkdown(markdown, extension);

        var fileBlocks = document.Descendants<FileContentBlock>().ToArray();
        fileBlocks.Should().HaveCount(1);

        var block = fileBlocks[0];
        block.Path.Should().Be("content:host/1/Submissions@MS-2024/folder/file.xlsx");
        block.Address.Should().Be("host/1");
    }

    #endregion
}
