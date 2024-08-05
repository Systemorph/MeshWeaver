using FluentAssertions;
using Markdig;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout.Markdown;
using OpenSmc.Messaging;
using Xunit.Abstractions;

namespace OpenSmc.Documentation.Test;

/// <summary>
/// The main class for testing documentation
/// </summary>
/// <param name="output"></param>
public class DocumentationTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configure the documentation service
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddDocumentation(doc => doc
            .WithEmbeddedResourcesFrom(GetType().Assembly, assembly => 
                assembly.WithXmlComments().WithDocument("Markdown","/Markdown")));
    }

    /// <summary>
    /// This is how to retrieve a file from documentation service
    /// </summary>
    /// <returns></returns>
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

    /// <summary>
    /// This tests the rendering of Layout Area Markdown
    /// </summary>
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
        html.Trim().Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
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
        var extension = new LayoutAreaMarkdownExtension(GetHost());
        var html = RenderMarkdown(extension, markdown);

        extension.MarkdownParser.Areas.Should().HaveCount(1);
        var area = extension.MarkdownParser.Areas[0];
        area.Area.Should().Be("MyArea");
        area.Layout.Should().Be(Doc);
        area.Reference.Layout.Should().Be(Doc);

        // Verify the results
        html.Trim().Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
    }


    private static string RenderMarkdown(LayoutAreaMarkdownExtension markdownExtension, string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder().Use(markdownExtension).Build();

        var html = Markdown.ToHtml(markdown, pipeline);
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

    /// <summary>
    /// Here we read the source from embedded assemblies
    /// </summary>
    /// <returns></returns>

    [HubFact]
    public void TryReadSource()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var type = GetType();
        var sourceByType = documentationService.GetSources(type.Assembly);
        sourceByType.Should().NotBeNull();
        var source = sourceByType.GetSource(typeof(DocumentationTest).FullName);
        source.Should().NotBeNull();
    }


    /// <summary>
    /// This tests reading debug info from the pdb
    /// </summary>
    [HubFact]
    public void TestDebugInfo()
    {
        var points = PdbMethods.ReadMethodSourceInfo(typeof(DocumentationTest).Assembly.Location, nameof(TestDebugInfo));
        points.Should().NotBeNull();
        points.Should().HaveCountGreaterThan(0);
    }

}
