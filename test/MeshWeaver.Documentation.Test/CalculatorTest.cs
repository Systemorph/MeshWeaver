using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests the calculator area using MeshNode and MarkdownContent.
/// </summary>
/// <param name="output"></param>
public class CalculatorTest(ITestOutputHelper output) : DocumentationTestBase(output)
{
    /// <summary>
    /// Tests that the Calculator markdown file exists and has expected content.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Calculator_NodeExists_WithMarkdownContent()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var node = await persistence.GetNodeAsync("Calculator", TestContext.Current.CancellationToken);

        node.Should().NotBeNull("Calculator node should exist");
        node!.NodeType.Should().Be("Markdown");
        node.Name.Should().Be("Calculator");

        var content = ExtractMarkdownContent(node);
        content.Should().NotBeNull("Node should have MarkdownContent");
        content!.Content.Should().NotBeNull("Content should not be null");
        content.PrerenderedHtml.Should().NotBeNull("PrerenderedHtml should not be null");
    }

    /// <summary>
    /// Tests the calculator area by executing code from the markdown.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CalculatorThroughMarkdown()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Get the calculator node
        var node = await persistence.GetNodeAsync("Calculator", TestContext.Current.CancellationToken);
        node.Should().NotBeNull("Calculator node should exist");

        // Extract MarkdownContent
        var markdownContent = ExtractMarkdownContent(node!);
        markdownContent.Should().NotBeNull("Node should have MarkdownContent");
        markdownContent!.Content.Should().NotBeNull("Content should not be null");
        markdownContent.PrerenderedHtml.Should().NotBeNull("PrerenderedHtml should not be null");

        Output.WriteLine($"Content: {markdownContent.Content}");
        Output.WriteLine($"PrerenderedHtml length: {markdownContent.PrerenderedHtml?.Length ?? 0}");
        Output.WriteLine($"CodeSubmissions count: {markdownContent.CodeSubmissions?.Count ?? 0}");

        // Create a kernel and submit the code
        var client = GetClient();
        var kernelAddress = AddressExtensions.CreateKernelAddress();

        if (markdownContent.CodeSubmissions != null && markdownContent.CodeSubmissions.Count > 0)
        {
            foreach (var submission in markdownContent.CodeSubmissions)
            {
                Output.WriteLine($"Submitting code: {submission.Code}");
                client.Post(submission, o => o.WithTarget(kernelAddress));
            }
        }

        // Replace placeholder in HTML and extract layout area addresses
        var html = markdownContent.PrerenderedHtml!
            .Replace(ExecutableCodeBlockRenderer.KernelAddressPlaceholder, kernelAddress.ToString());

        var layoutAreas = HtmlParser.ExtractDataAddressAttributes(html).ToList();
        layoutAreas.Should().NotBeEmpty("PrerenderedHtml should contain layout area references");

        Output.WriteLine($"Found {layoutAreas.Count} layout areas");
        foreach (var (addr, area) in layoutAreas)
        {
            Output.WriteLine($"  Address: {addr}, Area: {area}");
        }

        var (addressString, layoutArea) = layoutAreas.First();
        var address = client.GetAddress(addressString);

        // Get the stream for the calculator layout area
        var calcStream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(address, new(layoutArea));

        var control = await calcStream.GetControlStream(layoutArea)
            .Timeout(20.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Which;
        control = await calcStream.GetControlStream(stack.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // The calculator should show the sum (1 + 2 = 3)
        control.Should().BeOfType<MarkdownControl>()
            .Which.Markdown.ToString().Should().Contain("3");
    }

    /// <summary>
    /// Tests that markdown content is correctly parsed with code submissions.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Calculator_HasCodeSubmissions()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var node = await persistence.GetNodeAsync("Calculator", TestContext.Current.CancellationToken);
        node.Should().NotBeNull();

        var content = ExtractMarkdownContent(node!);
        content.Should().NotBeNull();

        // The calculator markdown should have executable code blocks
        content!.CodeSubmissions.Should().NotBeNull("Calculator should have code submissions");
        content.CodeSubmissions!.Count.Should().BeGreaterThan(0, "Calculator should have at least one code submission");

        Output.WriteLine($"Found {content.CodeSubmissions.Count} code submissions:");
        foreach (var submission in content.CodeSubmissions)
        {
            Output.WriteLine($"  Id: {submission.Id}");
            Output.WriteLine($"  Code: {submission.Code}");
        }
    }
}
