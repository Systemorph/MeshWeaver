using FluentAssertions;
using Markdig;
using MeshWeaver.Domain.Layout.Documentation;
using MeshWeaver.Fixture;
using MeshWeaver.Markdown;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;

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
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration) => 
        configuration.ConfigureDocumentationTestHost();

    /// <summary>
    /// This is how to retrieve a file from documentation service
    /// </summary>
    [HubFact]
    public async Task TestRetrievingFile()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var stream = documentationService.GetStream(EmbeddedDocumentationSource.Embedded, "MeshWeaver.Documentation.Test", "Readme.md");
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
        var extension = new LayoutAreaMarkdownExtension();
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
        var extension = new LayoutAreaMarkdownExtension();
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
        var extension = new LayoutAreaMarkdownExtension();
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
    public async Task TryReadSource()
    {
        var documentationService = GetHost().GetDocumentationService();
        documentationService.Should().NotBeNull();
        var type = GetType();
        var sourceByType = (PdbDocumentationSource)documentationService.GetSource(PdbDocumentationSource.Pdb, type.Assembly.GetName().Name);
        sourceByType.Should().NotBeNull();
        var fileName = sourceByType.FilesByType.GetValueOrDefault(typeof(DocumentationTest).FullName);
        fileName.Should().Be($"{nameof(DocumentationTest)}.cs");
        await using var stream = sourceByType.GetStream(fileName);
        stream.Should().NotBeNull();
        var content = await new StreamReader(stream).ReadToEndAsync();
        content.Should().NotBeNullOrWhiteSpace();
    }


    /// <summary>
    /// This tests reading debug info from the pdb
    /// </summary>
    [HubFact]
    public void TestDebugInfo()
    {
        var points = PdbDocumentationSource.ReadMethodSourceInfo(typeof(DocumentationTest).Assembly.Location, nameof(TestDebugInfo));
        points.Should().NotBeNull();
        points.Should().HaveCountGreaterThan(0);
    }

}
