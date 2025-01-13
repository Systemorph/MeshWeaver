using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;


public record Calculator
{
    [Description("This is the X value")] 
    public double X { get; init; }
    [Description("This is the Y value")]
    public double Y { get; init; }

}
public class EditorTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddLayout(layout => layout
            .WithView(nameof(EditorWithoutResult), EditorWithoutResult)
            .WithView(nameof(EditorWithResult), EditorWithResult)
            .WithView(nameof(EditorWithDelayedResult), EditorWithDelayedResult)
        );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    private UiControl EditorWithoutResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit<Calculator>(new Calculator());
    private UiControl EditorWithResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c => Controls.Markdown($"{c.X + c.Y}"));
    private UiControl EditorWithDelayedResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(), c =>
        {
            Thread.Sleep(100);
            return Controls.Markdown($"{c.X + c.Y} @ {DateTime.UtcNow.Second}:{DateTime.UtcNow.Millisecond}");
        });


    [Fact]
    public async Task TestEditorWithoutResult()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(nameof(EditorWithoutResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithoutResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a => 
                await area.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync())
            .ToArrayAsync();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

    }
    [Fact]
    public async Task TestEditorWithResult()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(nameof(EditorWithResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area
            .GetControlStream(stack.Areas.First().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;
        editor.Areas.Should()
            .HaveCount(2)
            .And.AllSatisfy(e =>
            {
                var skin = e.Skins.OfType<PropertySkin>().Should().ContainSingle().Subject;
                skin.Label.Should().NotBeNull();
                skin.Description.Should().NotBeNull();
            });
        var editorAreas = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a =>
                await area.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync(x => x is not null))
            .ToArrayAsync();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should()
            .AllBeOfType<NumberFieldControl>()
            ;

        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        control.Should().BeOfType<MarkdownControl>().Subject.Data.Should().Be("0");

        // update once ==> will issue "add", as 0 was not there
        area.UpdatePointer(1, editor.DataContext, new("/x"));
        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not MarkdownControl { Data: "0" });

        control.Should().BeOfType<MarkdownControl>().Subject.Data.Should().Be("1");

        // update once ==> will issue "replace"
        area.UpdatePointer(2, editor.DataContext, new("/x"));
        control = await area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            //.Timeout(10.Seconds())
            .FirstAsync(x => x is not MarkdownControl { Data: "1" });

        control.Should().BeOfType<MarkdownControl>().Subject.Data.Should().Be("2");
    }
    [Fact]
    public async Task TestEditorWithDelayed()
    {
        var client = GetClient();

        var workspace = client.GetWorkspace();
        var area = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(nameof(EditorWithDelayedResult)));
        var control = await area
            .GetControlStream(nameof(EditorWithDelayedResult))
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area
            .GetControlStream(stack.Areas.First().Area.ToString())
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var editor = control.Should().BeOfType<EditorControl>().Subject;

        
        var controlStream = area
            .GetControlStream(stack.Areas.Last().Area.ToString())
            .TakeUntil(x => x is MarkdownControl { Data: var data } && data.ToString()!.StartsWith("5"));


        // update once ==> will issue "replace"
        for (var i = 1; i <= 5; i++)
        {
            area.UpdatePointer(i, editor.DataContext, new("/x"));
        }

        var controls = await controlStream.ToArray();
        controls.Should().HaveCountLessThanOrEqualTo(3);
        controls.Last().Should().BeOfType<MarkdownControl>().Which.Data.ToString().Should().StartWith("5");
    }
}
