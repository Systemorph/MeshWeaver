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

    private SatelliteMainNodeCommitGuard Guard => new(Mesh.ServiceProvider);

    private string? Commit(MeshNode? before, MeshNode after, string ownerPath) =>
        Guard.Refusal(nameof(MeshNode), before, after, ownerPath, Person(Writer), Mesh.JsonSerializerOptions);

    private static MeshNode Comment(string mainNode) =>
        new("c1", Doc + "/_Comment") { Name = "c1", NodeType = "Comment", MainNode = mainNode };

    /// <summary>
    /// 🚨 A satellite CARRIED to another node's hub (a data change whose entity is not the receiving hub's
    /// own node) arrives with no predecessor, so the stored-vs-proposed comparison has nothing to compare.
    /// Committed at <see cref="Doc"/>'s hub, a comment carrying a chosen pointer is refused; one carrying
    /// the path-derived pointer is the control, and so is the same carried node committed at its own hub
    /// (an ordinary create, decided by the creation chain).
    /// </summary>
    [Fact]
    public void ACarriedSatelliteWithAChosenPointer_IsRefusedAtTheCarrier()
    {
        Commit(before: null, Comment(Locked), ownerPath: Doc).Should().NotBeNull(
            "a carrier holds no predecessor, so a satellite it commits may carry only a path-derived pointer");
        Commit(before: null, Comment(Doc), ownerPath: Doc).Should().BeNull(
            "the control: the path-derived pointer (the comment's owner) is what the satellite's location implies");
        Commit(before: null, Comment(Locked), ownerPath: CommentPath).Should().BeNull(
            "the control: at its OWN hub a satellite without a predecessor is a create, decided by the creation chain");
    }

    /// <summary>
    /// 🚨 Retype out, repoint, retype back: each write alone passes the stored-vs-proposed comparison,
    /// because the middle one changes the pointer while neither snapshot is a satellite. The write that
    /// makes the node a satellite again is where the chosen pointer is caught. The control is the same
    /// three writes with the pointer kept.
    /// </summary>
    [Fact]
    public void RetypingBackIntoASatelliteWithAChosenPointer_IsRefused()
    {
        var comment = Comment(Doc);
        var asMarkdown = comment with { NodeType = "Markdown" };
        var repointed = asMarkdown with { MainNode = Locked };

        Commit(comment, asMarkdown, CommentPath).Should().BeNull("retyping out keeps the pointer");
        Commit(asMarkdown, repointed, CommentPath).Should().BeNull("an ordinary node chooses its own pointer");
        Commit(repointed, repointed with { NodeType = "Comment" }, CommentPath).Should().NotBeNull(
            "a node that BECOMES a satellite may bring only a path-derived pointer — otherwise satellite "
            + "delegation is restored over the pointer the writer chose while it was not one");
        Commit(asMarkdown, asMarkdown with { NodeType = "Comment" }, CommentPath).Should().BeNull(
            "the control: retyping back with the path-derived pointer lands");
    }

    /// <summary>
    /// 🚨 The same three writes on a NESTED satellite: a comment on a thread
    /// (<c>Doc/_Thread/t1/_Comment/c1</c>), whose security owner is the thread <c>t1</c>. The storage
    /// owner of its first satellite segment (<c>Doc</c>) sits above <c>t1</c>, and a policy on <c>t1</c>
    /// can break inheritance, so a node that becomes a satellite may not bring that pointer either.
    /// The control is the thread itself.
    /// </summary>
    [Fact]
    public void RetypingANestedSatelliteBackWithTheOuterOwner_IsRefused()
    {
        const string thread = Doc + "/_Thread/t1";
        var nested = new MeshNode("c1", thread + "/_Comment")
            { Name = "c1", NodeType = "Markdown", MainNode = Doc };
        var ownPath = thread + "/_Comment/c1";

        Commit(nested, nested with { NodeType = "Comment" }, ownPath).Should().NotBeNull(
            "the outer document is above the nested satellite's security owner (the thread), so retyping back "
            + "with it would restore delegation above any inheritance-breaking policy on the thread");
        Commit(nested with { MainNode = thread }, nested with { MainNode = thread, NodeType = "Comment" }, ownPath)
            .Should().BeNull("the control: the thread, the nested satellite's security owner, is accepted");
    }

    /// <summary>
    /// 🚨 Four own-hub writes on a nested satellite: retype out keeping the thread as pointer, point it at
    /// ITSELF, retype back, then "normalise" the self-pointer to the outer document. The last write reads
    /// as the path-derived normalisation of an unset pointer, so the re-entry is where it must stop: a node
    /// that becomes a satellite points at its security owner, never at itself.
    /// </summary>
    [Fact]
    public void RetypingBackWithASelfPointer_IsRefused_SoTheOuterNormalisationIsNeverReached()
    {
        const string thread = Doc + "/_Thread/t1";
        var ownPath = thread + "/_Comment/c1";
        var asMarkdown = new MeshNode("c1", thread + "/_Comment") { Name = "c1", NodeType = "Markdown", MainNode = thread };
        var self = asMarkdown with { MainNode = ownPath };

        Commit(asMarkdown, self, ownPath).Should().BeNull("an ordinary node chooses its own pointer");
        Commit(self, self with { NodeType = "Comment" }, ownPath).Should().NotBeNull(
            "re-entering the satellite class with a self pointer would let the next write 'normalise' it to the "
            + "outer document, above the thread's own policy");
    }

    /// <summary>
    /// 🚨 A slash-only pointer is not "unset". <c>SatelliteAccessRule</c> falls back to the node's own path
    /// only for an EMPTY (or self) pointer, and delegates <c>"/"</c> to the ROOT scope, which can bypass a
    /// restrictive policy below it. So an update from <c>""</c> to <c>"/"</c> is a repoint, and a carried
    /// satellite may not bring <c>"/"</c>. The controls are a write that keeps the empty pointer and a
    /// carried write with the path-derived owner.
    /// </summary>
    [Fact]
    public void ASlashOnlyPointer_IsARepoint_NotAnUnsetOne()
    {
        var unset = Comment("");
        MainNodeTrust.Repoints(unset, unset with { MainNode = "/" }).Should().BeTrue(
            "the rule delegates '/' to the root scope, so moving from unset to '/' changes who decides access");
        Commit(unset, unset with { MainNode = "/" }, CommentPath).Should().NotBeNull(
            "an own-hub update from an empty pointer to '/' is refused like any other repoint");
        Commit(before: null, Comment("/"), ownerPath: Doc).Should().NotBeNull(
            "a carried satellite may not bring a slash-only pointer");

        MainNodeTrust.Repoints(unset, unset with { Name = "renamed" }).Should().BeFalse(
            "the control: keeping the empty pointer is not a repoint");
        Commit(before: null, Comment(Doc), ownerPath: Doc).Should().BeNull(
            "the control: a carried satellite with its path-derived owner lands");
    }
}
