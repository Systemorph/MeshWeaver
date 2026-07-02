#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Structural tests for the Slide node type (ships from the platform via
/// <c>AddGraph()</c> → <see cref="SlideNodeType.AddSlideType{TBuilder}"/>).
/// A deck is any parent whose children are Slide nodes, ordered by
/// <see cref="MeshNode.Order"/>. The Content area renders the 16:9 stage
/// (click advances to the next slide) plus a presenter bar with Prev /
/// "Slide n / N" / Deck / Present / Next; the Present area is the chrome-free
/// stage with only a corner counter.
/// </summary>
public class SlideLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// SlideContent must round-trip through the mesh hub's polymorphic serializer
    /// under its SHORT name discriminator — without the WithGraphTypes registration
    /// the content degrades to an untyped JsonElement across hub boundaries.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SlideContent_RoundTrips_WithShortTypeDiscriminator()
    {
        var options = Mesh.JsonSerializerOptions;
        var content = new SlideContent
        {
            Content = "# Welcome\n\n<svg width=\"10\" height=\"10\"></svg>",
            Notes = "Greet the audience.",
            Background = "linear-gradient(135deg, #667eea 0%, #764ba2 100%)"
        };

        var json = JsonSerializer.SerializeToElement<object>(content, options);
        json.GetProperty("$type").GetString().Should().Be(nameof(SlideContent),
            "SlideContent must serialize under its short type name");

        var back = JsonSerializer.Deserialize<object>(json.GetRawText(), options);
        back.Should().BeOfType<SlideContent>();
        ((SlideContent)back!).Should().Be(content);
    }

    /// <summary>
    /// Renders the MIDDLE slide of a 3-slide deck: prev/next must target the
    /// Order-adjacent siblings (Order 1 and 3 — NOT path-alphabetical neighbours),
    /// the counter must read "Slide 2 / 3", and the stage must carry a click action
    /// (click-to-advance).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ContentArea_MiddleSlide_NavigatesOrderedSiblings()
    {
        var (deck, first, middle, last) = await SeedDeck();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(SlideLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(middle), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        var stack = (StackControl)root!;

        var stagePath = AreaPath(stack, SlideLayoutAreas.StageArea);
        var barPath = AreaPath(stack, SlideLayoutAreas.PresenterBarArea);

        // Stage: clickable once the deck query has resolved a next slide.
        await stream.GetControlStream(stagePath)
            .Should().Within(30.Seconds()).Match(c => c is StackControl && c.IsClickable);

        // Presenter bar settles with Prev / Counter / Deck / Present / Next.
        var bar = (StackControl)(await stream.GetControlStream(barPath)
            .Should().Within(30.Seconds()).Match(c =>
                c is StackControl b && b.Areas.Count >= 5))!;

        var prev = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.PrevButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl))!;
        prev.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(first, SlideLayoutAreas.ContentArea),
            "Prev must target the Order-1 sibling");

        var next = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.NextButtonArea))
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl))!;
        next.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(last, SlideLayoutAreas.ContentArea),
            "Next must target the Order-3 sibling");

        await stream.GetControlStream(AreaPath(bar, SlideLayoutAreas.CounterArea))
            .Should().Within(10.Seconds()).Match(c =>
                c is LabelControl l && "Slide 2 / 3".Equals(l.Data?.ToString()),
            "the counter must reflect the Order-based position");

        var deckLink = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.DeckLinkArea))
            .Should().Within(10.Seconds()).Match(c => c is ButtonControl))!;
        deckLink.NavigateToHref!.ToString().Should().Be($"/{deck}",
            "the presenter bar links back to the deck parent");

        await CleanupDeck(deck, first, middle, last);
    }

    /// <summary>
    /// The Present area is chrome-free: stage + corner counter only, NO prev/next
    /// buttons — and the stage still click-advances (staying in Present mode).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PresentArea_IsChromeFree_WithClickableStage()
    {
        var (deck, first, middle, last) = await SeedDeck();

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(SlideLayoutAreas.PresentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(middle), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(c => c is StackControl);
        var stack = (StackControl)root!;

        // Exactly stage + counter — no presenter bar, no buttons.
        stack.Areas.Should().HaveCount(2, "Present renders only the stage and the counter overlay");
        foreach (var area in stack.Areas)
        {
            var name = area.Area?.ToString();
            name.Should().NotBeNullOrEmpty();
            var child = await stream.GetControlStream(name!)
                .Should().Within(10.Seconds()).Match(c => c != null);
            child.Should().NotBeOfType<ButtonControl>("Present mode is chrome-free");
        }

        // Stage click-advances once the deck query has resolved the next slide.
        await stream.GetControlStream(AreaPath(stack, SlideLayoutAreas.StageArea))
            .Should().Within(30.Seconds()).Match(c => c is StackControl && c.IsClickable);

        await CleanupDeck(deck, first, middle, last);
    }

    /// <summary>
    /// Seeds a deck: a Space parent with three Slide children whose Order (1,2,3)
    /// deliberately CONTRADICTS path-alphabetical order (end &lt; intro &lt; middle),
    /// created out of order — sibling navigation must follow MeshNode.Order.
    /// </summary>
    private async Task<(string Deck, string First, string Middle, string Last)> SeedDeck()
    {
        var deck = $"Deck{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Training Deck",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Training Deck" }
        }).Should().Emit();

        var first = $"{deck}/intro";
        var middle = $"{deck}/main";
        var last = $"{deck}/end";

        // Created out of order on purpose — Order, not creation or path order, rules.
        await CreateSlide(last, "The End", 3, "# Thanks!\n\nQuestions?");
        await CreateSlide(first, "Welcome", 1, "# Welcome\n\nTraining time.");
        await CreateSlide(middle, "Main", 2, "# The Big Idea\n\n- one\n- two");

        return (deck, first, middle, last);
    }

    private async Task CreateSlide(string path, string name, int order, string body)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = SlideNodeType.NodeType,
            Order = order,
            Content = new SlideContent { Content = body, Notes = $"Notes for {name}" }
        }).Should().Emit();

    private async Task CleanupDeck(string deck, params string[] slides)
    {
        foreach (var slide in slides)
            await NodeFactory.DeleteNode(slide).Should().Emit();
        await NodeFactory.DeleteNode(deck).Should().Emit();
    }

    /// <summary>
    /// Resolves the FULL area path of a named child area (area paths are rendered as
    /// <c>{parent}/{name}</c>; matching by suffix keeps the tests independent of the
    /// parent's own path).
    /// </summary>
    private static string AreaPath(StackControl parent, string name)
    {
        var path = parent.Areas
            .Select(a => a.Area?.ToString())
            .FirstOrDefault(p => p != null && p.EndsWith($"/{name}", StringComparison.Ordinal));
        path.Should().NotBeNull($"expected a child area named '{name}'");
        return path!;
    }
}
