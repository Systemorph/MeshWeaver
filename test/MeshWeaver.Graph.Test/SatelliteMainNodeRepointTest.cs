using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🛡️ An update never repoints a satellite's <c>MainNode</c>. The stored pointer is what the satellite
/// rule delegates every access decision to, so a write that could change it would let its writer decide
/// access by a pointer they picked: move a comment under a main node they cannot write to, or point it
/// at one whose grants it then borrows. A create already derives ownership from the path (#7601); this
/// is the update half, held both in the rule (<see cref="SatelliteAccessRule"/>) and at the owner's
/// commit point (<see cref="SatelliteMainNodeCommitGuard"/>), which every routed write meets.
///
/// <para>The writes are made the way a signed-in person makes them: a merge patch through
/// <c>GetMeshNodeStream(path).Update</c>, routed to the satellite's owning hub. The rule is the
/// chosen one — REFUSE any change, including to a target the writer may write — so both repoints are
/// refused. An ordinary update that keeps the pointer is the negative control.</para>
/// </summary>
public class SatelliteMainNodeRepointTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SatRepoint";
    private const string Doc = Partition + "/Doc";
    private const string Mine = Partition + "/Mine";
    private const string Locked = Partition + "/Locked";
    private const string CommentPath = Doc + "/_Comment/c1";

    /// <summary>Editor on <see cref="Doc"/> and on <see cref="Mine"/>; nothing on <see cref="Locked"/>.</summary>
    private const string Writer = "sat-repoint-writer";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // ConfigureMeshBase: no blanket Public-admin grant, so the writer holds only its own grants.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = Partition, NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("Mine", Partition) { Name = "Mine", NodeType = "Markdown" },
                new MeshNode("Locked", Partition) { Name = "Locked", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(Writer, Role.Editor.Id, Doc),
                AssignmentNodeFactory.UserRole(Writer, Role.Editor.Id, Mine));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static AccessContext Person(string id) => new() { ObjectId = id, Name = id };

    private async Task<MeshNode> SeedComment(CancellationToken ct)
    {
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("c1", Doc + "/_Comment")
            {
                Name = "c1", NodeType = "Comment", MainNode = Doc,
                Content = new Comment { Author = Writer, PrimaryNodePath = Doc, HighlightedText = "first" },
            }))
            .Should().Within(Budget).Emit("the comment must exist first", ct);
        return await Stored(ct);
    }

    private Task<MeshNode> Stored(CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(CommentPath)
                .Where(n => n is not null).Select(n => n!).Take(1))
            .Should().Within(Budget).Emit("the stored comment must load", ct);

    private Task<string> UpdateAs(string who, Func<MeshNode, MeshNode> change, CancellationToken ct)
    {
        Access.SetContext(Person(who));
        return Mesh.GetWorkspace().GetMeshNodeStream(CommentPath)
            .Update(change)
            .Take(1)
            .Select(_ => "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(Budget).Emit("the update must answer", ct);
    }

    /// <summary>
    /// 🚨 THE PIN. The writer may update the comment (Editor on its main node) but holds nothing on
    /// <see cref="Locked"/>. Repointing the comment there through the routed update is refused, and the
    /// stored pointer is unchanged.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ARepointToAMainNodeTheWriterCannotWrite_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedComment(ct);

        var answer = await UpdateAs(Writer, n => n with { MainNode = Locked }, ct);
        Output.WriteLine($"repoint to {Locked}: {answer}");
        answer.Should().StartWith("refused:", "an update never moves a satellite under a main node its writer cannot write to");
        (await Stored(ct)).MainNode.Should().Be(Doc, "the stored pointer still governs the comment");
    }

    /// <summary>
    /// The chosen rule is REFUSE, not re-validate: a repoint to a main node the writer CAN write is
    /// refused too, because the pointer is derived from where the satellite lives, never chosen by an
    /// update.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ARepointToTheWritersOwnTarget_IsRefusedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedComment(ct);

        var answer = await UpdateAs(Writer, n => n with { MainNode = Mine }, ct);
        Output.WriteLine($"repoint to {Mine}: {answer}");
        answer.Should().StartWith("refused:", "an update never chooses a satellite's main node, even one its writer may write");
        (await Stored(ct)).MainNode.Should().Be(Doc, "the stored pointer still governs the comment");
    }

    /// <summary>The negative control: the same writer's ordinary update, which keeps the pointer, lands.</summary>
    [Fact(Timeout = 90_000)]
    public async Task AnUpdateThatKeepsTheMainNode_Lands()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedComment(ct);

        (await UpdateAs(Writer, n => n with { Name = "c1 renamed" }, ct))
            .Should().Be("landed", "an update that does not touch MainNode is an ordinary edit");
        var stored = await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(CommentPath)
                .Where(n => n?.Name == "c1 renamed").Select(n => n!).Take(1))
            .Should().Within(Budget).Emit("the edit must be stored", ct);
        stored.MainNode.Should().Be(Doc);
    }

    /// <summary>
    /// The rule itself, asked as the update pipeline asks it (proposal + stored node): a changed
    /// pointer is refused even to a target the writer may write; the path-derived normalisation of an
    /// unset self-default to the satellite's owner is still decided as an ordinary update.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSatelliteRule_RefusesAChangedPointer_ButNotThePathDerivedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Update);
        rule.Should().NotBeNull("Comment registers the satellite rule");
        var stored = new MeshNode("c9", Doc + "/_Comment") { Name = "c9", NodeType = "Comment", MainNode = Doc };

        Task<bool> Decide(MeshNode existing, MeshNode proposal) =>
            rule!.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Update, Node = proposal, ExistingNode = existing, AccessContext = Person(Writer),
                }, Writer)
                .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);

        (await Decide(stored, stored with { MainNode = Mine })).Should().BeFalse(
            "a pointer the update chooses is refused, even to a main node the writer may write");
        (await Decide(stored, stored with { Name = "renamed" })).Should().BeTrue(
            "the control: an update that keeps the stored pointer delegates to it");

        var unset = stored with { MainNode = stored.Path };
        (await Decide(unset, unset with { MainNode = Doc })).Should().BeTrue(
            "the path-derived normalisation of an unset pointer to the satellite's owner is not a repoint");
    }
}
