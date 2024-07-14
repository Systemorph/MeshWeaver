using FluentAssertions;
using Markdig;
using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Documentation.Markdown;
using OpenSmc.Hub.Fixture;
using OpenSmc.Layout;
using OpenSmc.Messaging;
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
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Use<LayoutAreaExtension>(new LayoutAreaExtension(GetHost()))
            .Build();

        // Verify the results
        Markdig.Markdown.ToHtml(markdown, pipeline).Should().Be("<div>MyArea</div>");
    }
}
