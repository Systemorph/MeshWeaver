using FluentAssertions;
using Markdig;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Markdown;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Documentation.Test;

public class DocumentationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddDocumentation(doc => doc
            .WithEmbeddedResourcesFrom(GetType().Assembly, assembly => 
                assembly.WithXmlComments().WithFilePath("/Markdown")));
    }

    [HubFact]
    public async Task TestRetrievingFile()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var stream = documentationService.GetStream("OpenSmc.Documentation.Test.Markdown.Readme.md");
        stream.Should().NotBeNull();
        var content = await new StreamReader(stream).ReadToEndAsync();
        content.Should().NotBeNullOrEmpty();
    }

    [HubFact]
    public void BasicLayoutArea()
    {
        // Define a sample markdown string
        var markdown = "@(\"MyArea\")";
        var extension = new LayoutAreaMarkdownExtension(GetHost());
        var html = RenderMarkdown(extension, markdown);

        extension.MarkdownParser.Areas.Should().HaveCount(1);
        var area = extension.MarkdownParser.Areas[0];
        area.Area.Should().Be("MyArea");

        // Verify the results
        html.Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
    }

    private static string RenderMarkdown(LayoutAreaMarkdownExtension markdownExtension, string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();

        var html = Markdown.ToHtml(markdown, pipeline);
        return html;
    }

    [HubFact]
    public void TwoLayoutAreas()
    {
        // Define a sample markdown string
        var markdown = "@(\"Area1\")\n@(\"Area2\")";
        var extension = new LayoutAreaMarkdownExtension(GetHost());
        var html = RenderMarkdown(extension, markdown);

        extension.MarkdownParser.Areas.Should().HaveCount(2);
        var area1 = extension.MarkdownParser.Areas[0];
        var area2 = extension.MarkdownParser.Areas[1];
        area1.Area.Should().Be("Area1");
        area2.Area.Should().Be("Area2");

        // Verify the results
        html.Should().Contain(area1.DivId);
        html.Should().Contain(area2.DivId);
    }
}
