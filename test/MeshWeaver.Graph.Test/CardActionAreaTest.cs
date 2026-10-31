using System.Text.Json;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver.Plugins#4399 — a search result can carry an ACTION AREA: the default card,
/// painted from the result row, with one area beneath it that the result's OWN hub renders (an
/// install or purchase button). The alternative, <c>ItemArea</c>, hands the whole card to the
/// result's hub, so a shelf of N results paints nothing until N hubs have activated.
///
/// <para>These tests pin the wire contract the renderer reads: both controls cross the hub's
/// serializer with the action area AND the row caption intact. Negative control: a card with no
/// action area carries none, so every existing card renders as before.</para>
/// </summary>
public class CardActionAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private T RoundTrip<T>(T control) where T : UiControl
    {
        var json = JsonSerializer.Serialize<UiControl>(control, Mesh.JsonSerializerOptions);
        Output.WriteLine(json);
        return JsonSerializer.Deserialize<UiControl>(json, Mesh.JsonSerializerOptions)
            .Should().BeOfType<T>().Subject;
    }

    [Fact]
    public void ACardWithAnActionArea_KeepsTheRowCaption_AndTheActionArea_AcrossTheWire()
    {
        var node = new MeshNode("Anthropic") { Name = "Anthropic", Description = "Claude models", Icon = "🤖" };

        var card = RoundTrip(MeshNodeCardControl.FromNode(node, node.Path, disableNavigation: true).WithActionArea("CoverCta"));

        card.Title.Should().Be("Anthropic", "the card paints its name from the row, never from the node's hub");
        card.Description.Should().Be("Claude models", "and its description");
        card.ImageUrl.Should().Be("🤖", "and its icon");
        card.ItemArea.Should().BeNull("the card is NOT handed to the node's hub");
        card.ActionArea.Should().Be("CoverCta", "only the action area is the node hub's to render");
    }

    [Fact]
    public void ACardWithoutAnActionArea_CarriesNone()
    {
        var node = new MeshNode("Anthropic") { Name = "Anthropic" };

        var card = RoundTrip(MeshNodeCardControl.FromNode(node, node.Path));

        card.ActionArea.Should().BeNull("an ordinary card renders exactly as before");
    }

    [Fact]
    public void ASearchWithAnItemActionArea_KeepsIt_AndHandsNoCardToTheItemHub()
    {
        var search = RoundTrip(Controls.MeshSearch.WithHiddenQuery("nodeType:Store/Plugin partitions:all")
            .WithItemActionArea("CoverCta"));

        search.ItemActionArea.Should().NotBeNull();
        search.ItemActionArea!.ToString().Should().Be("CoverCta");
        search.ItemArea.Should().BeNull("the card comes from the row, so no per-result item area");
    }
}
