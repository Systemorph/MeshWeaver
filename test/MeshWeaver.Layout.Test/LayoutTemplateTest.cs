using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// <see cref="LayoutTemplate"/> is what a NodeType's in-mesh <c>Test/</c> cases use to assert that a
/// layout area is a TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"). In-mesh C# has
/// no test framework and cannot see a container's protected view list, so the platform reads it for
/// them — and this pins that it reads it right in both directions: a tree of controls is a template,
/// and a deferred view anywhere in it, however deep, is named.
/// </summary>
public class LayoutTemplateTest
{
    [Fact]
    public void ATreeOfControls_IsATemplate_AndEveryControlIsVisited()
    {
        var label = Controls.Label(new JsonPointerReference("name"));
        var tree = Controls.Stack
            .WithView(Controls.H2("Title"))
            .WithView(Controls.LayoutGrid.WithView(Controls.Stack.WithView(label), skin => skin.WithXs(12)));

        LayoutTemplate.DeferredViews(tree).Should().BeEmpty();
        LayoutTemplate.Descendants(tree).Should().Contain(label, "the walk reaches a label nested two containers deep");
        LayoutTemplate.Descendants(tree).Should().HaveCount(5);
    }

    [Fact]
    public void ADeferredView_IsNamed_WhereverItSits()
    {
        var tree = Controls.Stack
            .WithView(Controls.H2("Title"))
            .WithView(Controls.Stack
                .WithView(Controls.Body("static"))
                .WithView((_, _) => Observable.Return<UiControl?>(Controls.Markdown("arrives later"))));

        var deferred = LayoutTemplate.DeferredViews(tree);

        deferred.Should().ContainSingle("the one view that renders only once its observable emits")
            .Which.Should().StartWith("StackControl[1]: ");
        LayoutTemplate.Descendants(tree).OfType<MarkdownControl>().Should()
            .BeEmpty("a deferred view has no control yet, so the walk cannot reach one");
    }
}
