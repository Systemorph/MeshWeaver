using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;


public record Calculator(double X, double Y);
public class EditorTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration).AddLayout(layout => layout
            .WithView(nameof(EditorWithoutResult), EditorWithoutResult)
            .WithView(nameof(EditorWithResult), EditorWithResult)
        );
    }

    private UiControl EditorWithoutResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(1, 1));
    private UiControl EditorWithResult(LayoutAreaHost host, RenderingContext ctx) =>
        host.Hub.Edit(new Calculator(1, 1), (_,_,c) => Controls.Markdown($"Sum: {c.X + c.Y}"));


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
        editor.Areas.Should().HaveCount(2);
        var editorAreas = await editor.Areas.ToAsyncEnumerable()
            .SelectAwait(async a => 
                await area.GetControlStream(a.Area.ToString()).Timeout(5.Seconds()).FirstAsync())
            .ToArrayAsync();

        editorAreas.Should().HaveCount(2);
        editorAreas.Should().AllBeOfType<NumberFieldControl>();
    }
}
