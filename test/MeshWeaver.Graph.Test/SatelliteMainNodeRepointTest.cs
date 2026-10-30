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
        Commit(before: null, Comment(Doc), ownerPath: Doc).Should().NotBeNull(
            "a carrier cannot tell a create from a rewrite, so even the path-derived pointer is refused there");
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
        Commit(before: null, Comment(Doc), ownerPath: CommentPath).Should().BeNull(
            "the control: the same node at its own hub is a create, decided by the creation chain");
    }

    // ── THE INVARIANT, route by route ─────────────────────────────────────────────────────────────
    // For every non-System write that commits a satellite, the committed MainNode is the stored
    // satellite's pointer unchanged, or EXACTLY its path-derived security owner where nothing
    // satellite-shaped was stored. Every route that can put a pointer on a satellite is a row below;
    // a row that lands must leave the pointer at one of those two values.

    private const string Thread = Doc + "/_Thread/t1";
    private const string NestedPath = Thread + "/_Comment/c1";

    private static MeshNode Nested(string mainNode, string nodeType = "Comment") =>
        new("c1", Thread + "/_Comment") { Name = "c1", NodeType = nodeType, MainNode = mainNode };

    /// <summary>One write: the stored node (null for none), the node to commit, and the committing hub.</summary>
    public sealed record PointerRoute(string Name, MeshNode? Before, MeshNode After, string OwnerPath, bool Lands)
    {
        /// <inheritdoc />
        public override string ToString() => Name;
    }

    /// <summary>Every route that can set or change a satellite's main node.</summary>
    public static TheoryData<PointerRoute> PointerRoutes()
    {
        var unset = Comment("");
        var self = Comment(CommentPath);
        var stored = Comment(Doc);
        var offAncestry = Comment(Locked); // as System or the create chain may have written it
        var asMarkdown = stored with { NodeType = "Markdown" };
        return new TheoryData<PointerRoute>
        {
            // Update / merge patch at the satellite's own hub.
            new PointerRoute("update keeps the pointer", stored, stored with { Name = "renamed" }, CommentPath, true),
            new PointerRoute("update to a locked target", stored, Comment(Locked), CommentPath, false),
            new PointerRoute("update to the writer's own target", stored, Comment(Mine), CommentPath, false),
            new PointerRoute("update from a stored off-ancestry pointer to the path owner", offAncestry, Comment(Doc), CommentPath, false),
            new PointerRoute("update keeps a stored off-ancestry pointer", offAncestry, offAncestry with { Name = "renamed" }, CommentPath, true),
            // Normalisation of a pointer that was never set.
            new PointerRoute("unset to the exact owner", unset, Comment(Doc), CommentPath, true),
            new PointerRoute("self to the exact owner", self, Comment(Doc), CommentPath, true),
            new PointerRoute("unset nested to the OUTER storage owner", Nested(""), Nested(Doc), NestedPath, false),
            new PointerRoute("unset nested to its own owner (the thread)", Nested(""), Nested(Thread), NestedPath, true),
            new PointerRoute("unset to the root '/'", unset, Comment("/"), CommentPath, false),
            new PointerRoute("unset to the owner with a trailing slash", unset, Comment(Doc + "/"), CommentPath, false),
            new PointerRoute("unset to a case variant of the owner", unset, Comment(Doc.ToLowerInvariant()), CommentPath, false),
            new PointerRoute("unset to a '..' path naming the owner", unset, Comment(Doc + "/_Comment/.."), CommentPath, false),
            new PointerRoute("unset to an ancestor above the owner", unset, Comment(Partition), CommentPath, false),
            // Retype into the satellite class.
            new PointerRoute("retype in with the exact owner", asMarkdown, stored, CommentPath, true),
            new PointerRoute("retype in with a self pointer", asMarkdown with { MainNode = CommentPath }, Comment(CommentPath), CommentPath, false),
            new PointerRoute("retype in with an empty pointer", asMarkdown with { MainNode = "" }, Comment(""), CommentPath, false),
            new PointerRoute("retype in with a chosen pointer", asMarkdown with { MainNode = Locked }, Comment(Locked), CommentPath, false),
            new PointerRoute("retype in nested with the outer owner", Nested(Doc, "Markdown"), Nested(Doc), NestedPath, false),
            new PointerRoute("retype out keeps the pointer", stored, asMarkdown, CommentPath, true),
            new PointerRoute("retype out changes the pointer", stored, asMarkdown with { MainNode = Locked }, CommentPath, false),
            // Carried to another node's hub: refused whatever it carries.
            new PointerRoute("carried with the exact owner", null, Comment(Doc), Doc, false),
            new PointerRoute("carried with a chosen pointer", null, Comment(Locked), Doc, false),
            new PointerRoute("carried rewrite keeping the stored pointer", stored, stored with { Name = "x" }, Doc, false),
            new PointerRoute("carried with the root '/'", null, Comment("/"), Doc, false),
            // A create at the satellite's own hub is the creation chain's decision (SatelliteMainNodeLandingTest).
            new PointerRoute("create at its own hub", null, Comment(Doc), CommentPath, true),
        };
    }

    /// <summary>
    /// 🚨 THE AUDIT. Each route either is refused at the commit point, or lands leaving the pointer at the
    /// stored value or EXACTLY the path-derived security owner, so the access decision is the stored
    /// node's or the path owner's and never one a writer chose.
    /// </summary>
    [Theory]
    [MemberData(nameof(PointerRoutes))]
    public void TheInvariant_HoldsForEveryRouteThatSetsAPointer(PointerRoute route)
    {
        var refusal = Commit(route.Before, route.After, route.OwnerPath);
        Output.WriteLine($"{route.Name}: {refusal ?? "lands"}");
        if (!route.Lands)
        {
            refusal.Should().NotBeNull($"'{route.Name}' must be refused at the commit point");
            return;
        }
        refusal.Should().BeNull($"'{route.Name}' is a write the invariant allows");
        var committed = route.After.MainNode;
        var leftAsStored = route.Before is not null && string.Equals(route.Before.MainNode, committed, StringComparison.Ordinal);
        var isOwner = MainNodeTrust.IsOwnerOf(committed, route.After.Path);
        var isCreate = route.Before is null;
        (leftAsStored || isOwner || isCreate || route.After.NodeType == "Markdown").Should().BeTrue(
            $"'{route.Name}' landed, so its pointer must be the stored one or exactly the path-derived owner");
    }

    /// <summary>
    /// 🚨 Routed end to end: a nested comment stored with an EMPTY pointer (the create chain keeps
    /// <c>""</c>; it only rewrites a self pointer) is updated by a writer, through the comment's own
    /// stream, to the OUTER document. That would move delegation above the thread. It is refused, and the
    /// empty pointer is still stored.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task ANestedSatelliteStoredUnset_CannotBeNormalisedToTheOuterOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        await Access.RunAsSystem(() => NodeFactory.CreateNode(new MeshNode("c1", Thread + "/_Comment")
            {
                Name = "c1", NodeType = "Comment", MainNode = "",
                Content = new Comment { Author = Writer, PrimaryNodePath = Doc, HighlightedText = "nested" },
            }))
            .Should().Within(Budget).Emit("the nested comment must exist first", ct);

        Access.SetContext(Person(Writer));
        var answer = await Mesh.GetWorkspace().GetMeshNodeStream(NestedPath)
            .Update(n => n with { MainNode = Doc })
            .Take(1)
            .Select(_ => "landed")
            .Catch((Exception ex) => Observable.Return("refused:" + ex.GetType().Name + ": " + ex.Message))
            .Should().Within(Budget).Emit("the update must answer", ct);
        Output.WriteLine($"unset -> {Doc}: {answer}");
        answer.Should().StartWith("refused:", "a writer never moves a nested satellite's delegation above its own owner");

        var after = await Access.RunAsSystem(() => Mesh.GetWorkspace().GetMeshNodeStream(NestedPath)
                .Where(n => n is not null).Select(n => n!).Take(1))
            .Should().Within(Budget).Emit("the stored comment must load", ct);
        after.MainNode.Should().NotBe(Doc, "the outer owner was never committed");
    }
}
