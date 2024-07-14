using FluentAssertions;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Documentation.Markdown;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout;
using OpenSmc.Messaging;
using OpenSmc.Reflection;
using Xunit.Abstractions;

namespace OpenSmc.Documentation.Test;

public class DynamicMarkdownTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddDocumentation();
    }

    [HubFact]
    public void TestMarkdownParser()
    {
        // Define a sample markdown string
        var markdown = "@(\"MyArea\")";

        // Assuming ParseMarkdown is a method of a class that parses the markdown
        var extension = new LayoutAreaExtension(GetHost());
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Use(extension).Build();

        var html = Markdig.Markdown.ToHtml(markdown, pipeline);

        extension.Parser.Areas.Should().HaveCount(1);
        var area = extension.Parser.Areas[0];
        area.Area.Should().Be("MyArea");

        // Verify the results
        html.Should().Be($"<div id='{area.DivId}' class='layout-area'></div>");
    }
}
