using FluentAssertions;
using Markdig;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
using Xunit;
using Xunit.Abstractions;

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
        var markdown = "@(\"MyArea\")";
        var extension = new LayoutAreaMarkdownExtension(TestAddress);
        var html = RenderMarkdown(markdown, extension);

        extension.MarkdownParser.Areas.Should().HaveCount(1);
        var area = extension.MarkdownParser.Areas[0];
        area.Area.Should().Be("MyArea");

        // Verify the results
        html.Trim().Should().Be($"<div id='{area.DivId}' data-address='layout-area'></div>");
    }

    /// <summary>
    /// This tests the rendering of Layout Area Markdown
    /// </summary>
    [HubFact]
    public void LayoutAreaWithDocumentation()
    {
        const string Doc = nameof(Doc);
        // Define a sample markdown string
        var markdown = $"@(\"MyArea\"){{ Layout = \"{Doc}\"}}";
        var extension = new LayoutAreaMarkdownExtension(TestAddress);
        var html = RenderMarkdown( markdown, extension);

        extension.MarkdownParser.Areas.Should().HaveCount(1);
        var area = extension.MarkdownParser.Areas[0];
        area.Area.Should().Be("MyArea");
        area.Layout.Should().Be(Doc);
        area.Reference.Layout.Should().Be(Doc);

        // Verify the results
        html.Trim().Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
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
        var markdown = "@(\"Area1\")\n@(\"Area2\")";
        var extension = new LayoutAreaMarkdownExtension(TestAddress);
        var html = RenderMarkdown( markdown, extension);

        extension.MarkdownParser.Areas.Should().HaveCount(2);
        var area1 = extension.MarkdownParser.Areas[0];
        var area2 = extension.MarkdownParser.Areas[1];
        area1.Area.Should().Be("Area1");
        area2.Area.Should().Be("Area2");

        // Verify the results
        html.Should().Contain(area1.DivId);
        html.Should().Contain(area2.DivId);
    }


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
}
