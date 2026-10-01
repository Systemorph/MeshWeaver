using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="MeshNodeThumbnailControl"/> and <see cref="MeshNodeCardControl"/> carry a BINDABLE
/// title and description (<c>TitleBinding</c> / <c>DescriptionBinding</c>, set by <c>BindToNode</c>,
/// <c>BindTitle</c>, <c>BindDescription</c>) — so a card captioned from data that is NOT its own
/// node's name (an access-assignment row captioned from the assignment, a membership card captioned
/// from the membership) renders at once instead of being built from a node the area loaded first
/// (Doc/GUI/DataBinding → "Binding a rich control to a node field").
///
/// <para>The pointers are resolved through the seam the renderers read through
/// (<see cref="LayoutAreaReference.TryParseMeshNodeDataContext"/> → <see cref="MeshNodeBindingExtensions.Bind"/>).
/// Negative controls: <c>FromNode</c> — the hub-baked shape — carries no binding at all, and a card
/// whose title is bound to one node does not move when a DIFFERENT node changes.</para>
/// </summary>
public class NodeBoundCardControlsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<string> Seed(string json, string? description = null)
    {
        var id = NewId("card-");
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = id,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Description = description,
            Content = JsonSerializer.Deserialize<JsonElement>(json),
        }).Should().Within(TestTimeouts.Convergence).Emit();
        return $"{TestPartition}/{id}";
    }

    private IObservable<string?> Resolve(object? binding, string? dataContext)
    {
        var pointer = binding.Should().BeOfType<JsonPointerReference>().Subject;
        var ctx = LayoutAreaReference.TryParseMeshNodeDataContext(dataContext);
        ctx.Should().NotBeNull("the card's DataContext is the node-bound context the renderer branches on");
        return MeshNodeBindingExtensions
            .Bind(Mesh, ctx!.Value.NodePath, ctx.Value.BindContent, ctx.Value.SubPath, pointer)
            .Select(v => v is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null);
    }

    private IObservable<MeshNode> SetContent(string path, string json) =>
        Mesh.GetMeshNodeStream(path).Update(n => n with { Content = JsonSerializer.Deserialize<JsonElement>(json) });

    [Fact]
    public async Task AThumbnailCaptionedFromAnotherNode_RendersTheBoundTitle_AndFollowsAChange()
    {
        // The access-assignment shape: the card shows the SUBJECT, captioned from the ASSIGNMENT.
        var assignment = await Seed("""{"displayName":"Ada Lovelace","note":"editor since 2025"}""");
        var card = new MeshNodeThumbnailControl($"{TestPartition}/ada", "ada")
            .BindToNode(assignment, titleField: "displayName", descriptionField: "note");

        card.NodePath.Should().Be($"{TestPartition}/ada", "the card still navigates to (and draws the avatar of) its subject");
        var title = Resolve(card.TitleBinding, card.DataContext).Replay();
        var description = Resolve(card.DescriptionBinding, card.DataContext).Replay();
        using var c1 = title.Connect();
        using var c2 = description.Connect();

        await title.Should().Within(TestTimeouts.Convergence).Match(t => t == "Ada Lovelace", "the bound title renders");
        await description.Should().Within(TestTimeouts.Convergence).Match(d => d == "editor since 2025", "the bound description renders");

        await SetContent(assignment, """{"displayName":"Ada King","note":"editor since 2025"}""")
            .Should().Within(TestTimeouts.Convergence).Emit();

        (await title.Should().Within(TestTimeouts.Convergence).Match(t => t == "Ada King", "a bound title FOLLOWS the node"))
            .Should().Be("Ada King");
    }

    [Fact]
    public async Task ACardWhoseDescriptionIsBoundToTheNodesOwnDescription_FollowsIt()
    {
        var path = await Seed("{}", description: "first line");
        var card = new MeshNodeCardControl(path).BindToNode(path, titleField: null, descriptionField: nameof(MeshNode.Description), bindContent: false);

        card.TitleBinding.Should().BeNull("a null field leaves that slot unbound");
        var description = Resolve(card.DescriptionBinding, card.DataContext).Replay();
        using var c = description.Connect();
        await description.Should().Within(TestTimeouts.Convergence).Match(d => d == "first line", "bound to the node's top-level Description");

        await Mesh.GetMeshNodeStream(path).Update(n => n with { Description = "second line" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        (await description.Should().Within(TestTimeouts.Convergence).Match(d => d == "second line", "the card follows"))
            .Should().Be("second line");
    }

    /// <summary>NEGATIVE CONTROLS — the baked shape has nothing to follow, and a binding on one node
    /// does not move when another node changes.</summary>
    [Fact]
    public async Task NegativeControl_FromNodeIsBaked_AndABindingIgnoresOtherNodes()
    {
        var baked = MeshNodeThumbnailControl.FromNode(MeshNode.FromPath($"{TestPartition}/x") with { Name = "X" }, "x");
        baked.TitleBinding.Should().BeNull("FromNode copies the name in — the frozen copy this closes");
        baked.DataContext.Should().BeNull();

        var bound = await Seed("""{"displayName":"Bound"}""");
        var unrelated = await Seed("""{"displayName":"Unrelated"}""");
        var card = new MeshNodeThumbnailControl(bound, "fallback").BindToNode(bound, "displayName");

        var title = Resolve(card.TitleBinding, card.DataContext).Replay();
        using var c = title.Connect();
        await title.Should().Within(TestTimeouts.Convergence).Match(t => t == "Bound", "control: it renders");

        await SetContent(unrelated, """{"displayName":"Changed elsewhere"}""").Should().Within(TestTimeouts.Convergence).Emit();
        await title.Where(t => t == "Changed elsewhere").Should().NotEmit(TimeSpan.FromSeconds(2),
            "a change to a different node never reaches this card's title");
    }

    [Fact]
    public void BoundCardsSurviveSerialization()
    {
        var options = Mesh.JsonSerializerOptions;
        var thumb = new MeshNodeThumbnailControl("p/subject", "subject").BindToNode("p/assignment", "displayName", "note");
        var back = JsonSerializer.Deserialize<MeshNodeThumbnailControl>(
            JsonSerializer.Serialize(thumb, options), options)!;
        back.TitleBinding.Should().Be(new JsonPointerReference("displayName"));
        back.DescriptionBinding.Should().Be(new JsonPointerReference("note"));
        back.DataContext.Should().Be(thumb.DataContext);

        var card = new MeshNodeCardControl("p/n").BindTitle(new JsonPointerReference("/data/\"title\""));
        var cardBack = JsonSerializer.Deserialize<MeshNodeCardControl>(
            JsonSerializer.Serialize(card, options), options)!;
        cardBack.TitleBinding.Should().Be(new JsonPointerReference("/data/\"title\""));
    }
}
