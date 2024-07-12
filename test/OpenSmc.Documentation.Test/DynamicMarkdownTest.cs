using FluentAssertions;
using OpenSmc.Documentation.Markdown;
using OpenSmc.Hub.Fixture;
using Xunit.Abstractions;

namespace OpenSmc.Documentation.Test;

public class DynamicMarkdownTest(ITestOutputHelper output) : HubTestBase(output)
{

    [HubFact]
    public void TestMarkdownParser()
    {
        // Define a sample markdown string
        var markdown =
            @"
[LayoutArea Area=""Main"", Options=""{ 'key': 'value' }""]
[LayoutArea Area=""Sidebar"", SourceReference=""SomeReference""]";

        // Assuming ParseMarkdown is a method of a class that parses the markdown
        var parser = new MarkdownComponentParser(GetHost());
        var components = parser.ParseMarkdown(markdown);

        // Verify the results
        components.Should().NotBeNull();
        components.Should().HaveCount(2);
        components[0].Should().BeOfType<LayoutAreaComponentInfo>();
        ((LayoutAreaComponentInfo)components[0]).Area.Should().Be("Main");
        ((LayoutAreaComponentInfo)components[0]).Options.Should().ContainKey("key");

    }
}
