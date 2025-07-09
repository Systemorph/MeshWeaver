using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Documentation.LayoutAreas;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Tests the distribution statistics dialog
/// </summary>
/// <param name="output"></param>
public class DistributionStatisticsTest(ITestOutputHelper output) : DocumentationTestBase(output)
{
    /// <summary>
    /// Tests the distribution statistics dialog
    /// </summary>
    [Fact]
    public async Task DistributionStatistics()
    {
        var client = GetClient();
        var area = nameof(DistributionStatisticsArea.DistributionStatistics);
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(Address, new LayoutAreaReference(area));
        var control = await stream.GetControlStream(area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // we have a total of 4 areas: 1. basic input, 2. distribution, 3. button, 4. results
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().HaveCount(4);

        // let's find the selection control in the first area.
         control = await stream.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
         
         var basicInputEditor = control
                .Should().BeOfType<EditorControl>().Which;

         basicInputEditor.Areas.Should().HaveCount(2);

         control = await stream.GetControlStream(basicInputEditor.Areas.Last().Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);
        var select = control.Should().BeOfType<SelectControl>().Which;

        // let's get the options to be displayed in the combobox.
        var pointer = select.Options.Should().BeOfType<JsonPointerReference>().Which;
        var options = (await stream.Reduce(pointer)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null))
            .Value;
        options.ValueKind.Should().Be(JsonValueKind.Array);
        var jsonArray = options.EnumerateArray().ToArray();
        jsonArray.Should().HaveCountGreaterThan(1);

        var selectionPointer = select.Data.Should().BeOfType<JsonPointerReference>().Which;
        var absoluteSelectionPointer =
            selectionPointer with { Pointer = $"{basicInputEditor.DataContext}/{selectionPointer.Pointer}" };
        var selection = (await stream.Reduce(absoluteSelectionPointer)
                .Timeout(10.Seconds())
                .FirstAsync(x => x is not null))
            .Value;

        selection.ToString().Should().Be("Pareto");

        // let's find the distribution control in the second area.
        control = await stream.GetControlStream(stack.Areas[1].Area.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var distributionEditor = control.Should().BeOfType<EditorControl>().Which;
        distributionEditor.Areas.Should().HaveCount(2);
        distributionEditor.DataContext.Should().Be(LayoutAreaReference.GetDataPointer("Distribution"));


        // let's find the distribution.
        var distribution = (await stream.Reduce(new JsonPointerReference(distributionEditor.DataContext))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null)).Value;

        distribution.GetProperty("$type").ToString().Should().Contain("Pareto");

        // let's check we get the placeholder for the results section

        var resultArea = stack.Areas[3].Area.ToString();
        control = await stream.GetControlStream(resultArea)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var results = control.Should().BeOfType<MarkdownControl>().Which;
        results.Markdown.Should().NotBeNull();

        // let's find the button and click
        var buttonArea = stack.Areas[2].Area;
        control = await stream.GetControlStream(buttonArea.ToString()!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<ButtonControl>();
        client.Post(new ClickedEvent(buttonArea.ToString()!, stream.StreamId), o => o.WithTarget(stream.Owner));

        control = await stream.GetControlStream(resultArea)
            .Where(x => x != null)
            .Cast<object>()
            .Timeout(10.Seconds())
            .OfType<MarkdownControl>()
            .FirstAsync(md => md.Markdown.ToString()!.Contains("Mean"))!;

        results = control.Should().BeOfType<MarkdownControl>().Which;
        var paretoResults = results.Markdown;
        
        // now let's change the distribution type
        stream.UpdatePointer(JsonSerializer.SerializeToNode("LogNormal"), basicInputEditor.DataContext, selectionPointer);
        
        // which should change the distribution
        distribution = (await stream.Reduce(new JsonPointerReference(distributionEditor.DataContext))
            .Timeout(10.Seconds())
            .Select(x => x.Value)
            .FirstAsync(x => !x.GetProperty("$type").ToString().Contains("Pareto")));

        distribution.GetProperty("$type").ToString().Should().Contain("LogNormal");
        client.Post(new ClickedEvent(buttonArea.ToString()!, stream.StreamId), o => o.WithTarget(stream.Owner));

        control = await stream.GetControlStream(resultArea)
            .Where(x => x != null)
            .Cast<object>()
            .Timeout(10.Seconds())
            .OfType<MarkdownControl>()
            .FirstAsync(md => md.Markdown != paretoResults)!;
        results = control.Should().BeOfType<MarkdownControl>().Which;
        results.Markdown.ToString().Should().Contain("Mean");
    }


    /// <summary>
    /// Configures the client
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }
}

