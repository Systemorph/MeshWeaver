using System.Linq;
using FluentAssertions;
using Markdig;
using Markdig.Syntax;
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
}
