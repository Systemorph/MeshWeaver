#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
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
/// Structural tests for the Deck node type (ships from the platform via
/// <c>AddGraph()</c> → <see cref="DeckNodeType.AddDeckType{TBuilder}"/>).
/// A Deck declares its slide ORDER EXTERNALLY, in <see cref="DeckContent.Slides"/> on the
/// deck node itself — NOT on each slide. When a Slide's parent is a Deck with a non-empty
/// manifest, the Slide views resolve prev/next/index/count from that manifest, overriding
/// the sibling <see cref="MeshNode.Order"/> fallback. Slides under a non-Deck parent keep
/// ordering by <see cref="MeshNode.Order"/>.
/// </summary>
public class DeckLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// DeckContent must round-trip through the mesh hub's polymorphic serializer under its
    /// SHORT name discriminator — without the WithGraphTypes registration the manifest
    /// degrades to an untyped JsonElement across hub boundaries and the slide views can't
    /// read the parent deck's order.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void DeckContent_RoundTrips_WithShortTypeDiscriminator()
    {
        var options = Mesh.JsonSerializerOptions;
        var content = new DeckContent
        {
            Title = "Intro to Widgets",
            Description = "A short deck.",
            Slides = ["c", "a", "b"]
        };

        var json = JsonSerializer.SerializeToElement<object>(content, options);
        json.GetProperty("$type").GetString().Should().Be(nameof(DeckContent),
            "DeckContent must serialize under its short type name");

        var back = JsonSerializer.Deserialize<object>(json.GetRawText(), options);
        back.Should().BeOfType<DeckContent>();
        var recovered = (DeckContent)back!;
        // Record equality compares the ImmutableList field by reference, so assert the
        // fields individually — the external, ordered manifest is what must survive.
        recovered.Title.Should().Be(content.Title);
        recovered.Description.Should().Be(content.Description);
        recovered.Slides.Should().Equal(new[] { "c", "a", "b" },
            "the external order must survive the round-trip");
    }

    /// <summary>
    /// A Deck parent whose manifest is <c>[c, a, b]</c> orders its slides c → a → b even
    /// though their <see cref="MeshNode.Order"/> (a=1, b=2, c=3) says otherwise. Rendering
    /// the manifest-MIDDLE slide (<c>a</c>) must resolve prev = <c>c</c>, next = <c>b</c>,
    /// and counter "Slide 2 / 3" — proving order is externalized to the deck, not the slides.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ManifestOrder_OverridesSlideOrder()
    {
        var (space, _, a, b, c) = await SeedDeck(slides: ["c", "a", "b"]);

        var workspace = GetClient(client => client.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(SlideLayoutAreas.ContentArea);
        // Render slide "a" — manifest-middle (position 2 of c,a,b), yet Order-1.
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(a), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(control => control is StackControl);
        var stack = (StackControl)root!;

        var barPath = AreaPath(stack, SlideLayoutAreas.PresenterBarArea);
        var bar = (StackControl)(await stream.GetControlStream(barPath)
            .Should().Within(30.Seconds()).Match(control =>
                control is StackControl b2 && b2.Areas.Count >= 5))!;

        var prev = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.PrevButtonArea))
            .Should().Within(10.Seconds()).Match(control => control is ButtonControl))!;
        prev.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(c, SlideLayoutAreas.ContentArea),
            "the manifest puts 'c' before 'a', regardless of MeshNode.Order");

        var next = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.NextButtonArea))
            .Should().Within(10.Seconds()).Match(control => control is ButtonControl))!;
        next.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(b, SlideLayoutAreas.ContentArea),
            "the manifest puts 'b' after 'a', regardless of MeshNode.Order");

        await stream.GetControlStream(AreaPath(bar, SlideLayoutAreas.CounterArea))
            .Should().Within(10.Seconds()).Match(control =>
                control is LabelControl label && "Slide 2 / 3".Equals(label.Data?.ToString()),
            "the counter must reflect the manifest position (2 of 3), not the Order position");

        await CleanupDeck(space);
    }

    /// <summary>
    /// Fallback: slides under a NON-Deck parent (a Space) keep ordering by
    /// <see cref="MeshNode.Order"/>. Rendering the Order-2 slide must resolve the Order-1 and
    /// Order-3 siblings as prev/next — proving the Deck-manifest override does NOT disturb the
    /// existing sibling-Order behavior.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NonDeckParent_FallsBackToSlideOrder()
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Training Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Training Space" }
        }).Should().Emit();

        // Slides directly under the Space (a non-Deck parent), created out of order; Order,
        // not path or creation order, must rule. Path-alphabetical is end < intro < mid.
        var intro = $"{space}/intro";
        var mid = $"{space}/mid";
        var end = $"{space}/end";
        await CreateSlide(end, "End", 3, "# Thanks!");
        await CreateSlide(intro, "Welcome", 1, "# Welcome");
        await CreateSlide(mid, "Main", 2, "# Main");

        var workspace = GetClient(client => client.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference(SlideLayoutAreas.ContentArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(mid), reference);

        var root = await stream.GetControlStream(reference.Area!)
            .Should().Within(30.Seconds()).Match(control => control is StackControl);
        var stack = (StackControl)root!;

        var barPath = AreaPath(stack, SlideLayoutAreas.PresenterBarArea);
        var bar = (StackControl)(await stream.GetControlStream(barPath)
            .Should().Within(30.Seconds()).Match(control =>
                control is StackControl b && b.Areas.Count >= 5))!;

        var prev = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.PrevButtonArea))
            .Should().Within(10.Seconds()).Match(control => control is ButtonControl))!;
        prev.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(intro, SlideLayoutAreas.ContentArea),
            "with a non-Deck parent, prev is the Order-1 sibling");

        var next = (ButtonControl)(await stream
            .GetControlStream(AreaPath(bar, SlideLayoutAreas.NextButtonArea))
            .Should().Within(10.Seconds()).Match(control => control is ButtonControl))!;
        next.NavigateToHref!.ToString().Should().Be(
            MeshNodeLayoutAreas.BuildUrl(end, SlideLayoutAreas.ContentArea),
            "with a non-Deck parent, next is the Order-3 sibling");

        await NodeFactory.DeleteNode(space).Should().Emit();
    }

    /// <summary>
    /// Seeds a Deck with three Slide children. The Deck's <see cref="DeckContent.Slides"/>
    /// manifest declares the order (<paramref name="slides"/>), while each Slide's
    /// <see cref="MeshNode.Order"/> (a=1, b=2, c=3) deliberately CONTRADICTS it and the slides
    /// are created out of order. Everything lives inside a top-level Space partition.
    /// </summary>
    private async Task<(string Space, string Deck, string A, string B, string C)> SeedDeck(
        ImmutableList<string> slides)
    {
        var space = $"Space{Guid.NewGuid():N}"[..16];
        await NodeFactory.CreateNode(MeshNode.FromPath(space) with
        {
            Name = "Training Space",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Training Space" }
        }).Should().Emit();

        var deck = $"{space}/widgets";
        await NodeFactory.CreateNode(MeshNode.FromPath(deck) with
        {
            Name = "Widgets Deck",
            NodeType = DeckNodeType.NodeType,
            Content = new DeckContent { Title = "Widgets", Slides = slides }
        }).Should().Emit();

        var a = $"{deck}/a";
        var b = $"{deck}/b";
        var c = $"{deck}/c";
        // Created out of order, Orders contradict the manifest — the manifest must rule.
        await CreateSlide(c, "Gamma", 3, "# Gamma");
        await CreateSlide(a, "Alpha", 1, "# Alpha");
        await CreateSlide(b, "Beta", 2, "# Beta");

        return (space, deck, a, b, c);
    }

    private async Task CreateSlide(string path, string name, int order, string body)
        => await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = SlideNodeType.NodeType,
            Order = order,
            Content = new SlideContent { Content = body, Notes = $"Notes for {name}" }
        }).Should().Emit();

    // Deleting the Space removes its whole partition (and every node inside it).
    private async Task CleanupDeck(string space)
        => await NodeFactory.DeleteNode(space).Should().Emit();

    /// <summary>
    /// Resolves the FULL area path of a named child area (area paths render as
    /// <c>{parent}/{name}</c>; matching by suffix keeps the tests independent of the
    /// parent's own path).
    /// </summary>
    private static string AreaPath(StackControl parent, string name)
    {
        var path = parent.Areas
            .Select(area => area.Area?.ToString())
            .FirstOrDefault(p => p != null && p.EndsWith($"/{name}", StringComparison.Ordinal));
        path.Should().NotBeNull($"expected a child area named '{name}'");
        return path!;
    }
}
