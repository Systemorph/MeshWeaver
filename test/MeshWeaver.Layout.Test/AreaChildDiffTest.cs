using System.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Repro for #732 / #733 at the layout-stream seam.
/// </summary>
public class AreaChildDiffTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string ChangingStack = nameof(ChangingStack);
    private const string ChangingEmbed = nameof(ChangingEmbed);

    private readonly ReplaySubject<UiControl?> stackViews = new(1);
    private readonly ReplaySubject<UiControl?> embedViews = new(1);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(ChangingStack, (_, _) => stackViews)
                .WithView(ChangingEmbed, (_, _) => embedViews));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private ISynchronizationStream<JsonElement> OpenStream(string area)
        => GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));

    [HubFact]
    public async Task InsertingChildAtFront_KeepsEveryChildDistinct()
    {
        stackViews.OnNext(Controls.Stack.WithView(Controls.Html("all-steps"), "AllSteps"));
        var stream = OpenStream(ChangingStack);

        var first = await stream.GetControlStream(ChangingStack)
            .Should().Within(10.Seconds()).Match(x => x is StackControl);
        first.Should().BeOfType<StackControl>()
            .Which.Areas.Select(a => a.Id!.ToString()).Should().Equal("AllSteps");

        var allSteps = await stream.GetControlStream($"{ChangingStack}/AllSteps")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        allSteps.Should().BeOfType<HtmlControl>().Which.Data!.ToString().Should().Be("all-steps");

        stackViews.OnNext(Controls.Stack
            .WithView(Controls.Html("back"), "Back")
            .WithView(Controls.Html("all-steps"), "AllSteps"));

        var second = await stream.GetControlStream(ChangingStack)
            .Should().Within(10.Seconds()).Match(x => x is StackControl { Areas.Count: 2 });
        second.Should().BeOfType<StackControl>()
            .Which.Areas.Select(a => a.Id!.ToString()).Should().Equal("Back", "AllSteps");

        var back = await stream.GetControlStream($"{ChangingStack}/Back")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        back.Should().BeOfType<HtmlControl>().Which.Data!.ToString().Should().Be("back");
    }

    [HubFact]
    public async Task ReplacingNamedChildSet_RemovesTheOldChild()
    {
        stackViews.OnNext(Controls.Stack.WithView(Controls.Html("prose"), "Prose"));
        var stream = OpenStream(ChangingStack);

        await stream.GetControlStream($"{ChangingStack}/Prose")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);

        stackViews.OnNext(Controls.Stack
            .WithView(Controls.Html("step"), "Step")
            .WithView(Controls.Html("rail"), "Rail")
            .WithView(Controls.Html("stage"), "Stage"));

        var second = await stream.GetControlStream(ChangingStack)
            .Should().Within(10.Seconds()).Match(x => x is StackControl { Areas.Count: 3 });
        second.Should().BeOfType<StackControl>()
            .Which.Areas.Select(a => a.Id!.ToString()).Should().Equal("Step", "Rail", "Stage");

        var stage = await stream.GetControlStream($"{ChangingStack}/Stage")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        stage.Should().BeOfType<HtmlControl>().Which.Data!.ToString().Should().Be("stage");

        var prose = await stream.GetControlStream($"{ChangingStack}/Prose")
            .Should().Within(10.Seconds()).Match(_ => true);
        prose.Should().BeNull("the removed child must not linger in the client store");
    }

    [HubFact]
    public async Task SiblingAreaSharingAPrefix_SurvivesTheOtherAreaRerendering()
    {
        // "ChangingStack/Step" and "ChangingStack/Step2" share a raw string prefix but are
        // SIBLINGS. Re-rendering the container must not delete the prefix-sharing sibling.
        stackViews.OnNext(Controls.Stack
            .WithView(Controls.Stack.WithView(Controls.Html("inner"), "Inner"), "Step")
            .WithView(Controls.Html("sibling"), "Step2"));
        var stream = OpenStream(ChangingStack);

        await stream.GetControlStream($"{ChangingStack}/Step/Inner")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);

        var sibling = await stream.GetControlStream($"{ChangingStack}/Step2")
            .Should().Within(10.Seconds()).Match(x => x is HtmlControl);
        sibling.Should().BeOfType<HtmlControl>().Which.Data!.ToString().Should().Be("sibling");
    }

    [HubFact]
    public async Task ChangingOnlyTheReference_EmitsTheNewLayoutAreaControl()
    {
        var target = CreateHostAddress("target");
        embedViews.OnNext(Controls.Stack
            .WithView(Controls.LayoutArea(target, "Structure"), "Stage"));
        var stream = OpenStream(ChangingEmbed);

        var firstEmbed = await stream.GetControlStream($"{ChangingEmbed}/Stage")
            .Should().Within(10.Seconds()).Match(x => x is LayoutAreaControl);
        firstEmbed.Should().BeOfType<LayoutAreaControl>()
            .Which.Reference.Area.Should().Be("Structure");

        embedViews.OnNext(Controls.Stack
            .WithView(Controls.LayoutArea(target, "Economics"), "Stage"));

        var secondEmbed = await stream.GetControlStream($"{ChangingEmbed}/Stage")
            .Should().Within(10.Seconds())
            .Match(x => x is LayoutAreaControl { Reference.Area: "Economics" });
        secondEmbed.Should().BeOfType<LayoutAreaControl>()
            .Which.Reference.Area.Should().Be("Economics");
    }
}
