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
            .WithEmbeddedResourcesFrom(GetType().Assembly, assembly => assembly.WithXmlComments().WithFilePath("/Markdown")));
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
    public void TestMarkdownParser()
    {
        // Define a sample markdown string
        var markdown = "@(\"MyArea\")";
        // Assuming ParseMarkdown is a method of a class that parses the markdown
        var extension = new LayoutAreaExtension(GetHost());
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Use(extension).Build();

        var html = Markdown.ToHtml(markdown, pipeline);

        extension.Parser.Areas.Should().HaveCount(1);
        var area = extension.Parser.Areas[0];
        area.Area.Should().Be("MyArea");

        // Verify the results
        html.Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
    }
}
