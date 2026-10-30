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
/// 🛡️ A satellite's <c>MainNode</c> the WRITER chooses is delegated to alone only when it is the
/// satellite's OWNER — the path before its first <c>_</c> segment, compared ordinally (review of #7601).
/// Any other pointer must also pass the permission check where the satellite lands:
/// <list type="bullet">
///   <item><description>a case-variant of the owner is a different node, since paths and scopes are
///     keyed ordinally;</description></item>
///   <item><description>an ancestor further up is not the owner, since a scope in between can break
///     the inherited grant.</description></item>
/// </list>
/// A STORED pointer still governs: deciding an update about the stored satellite (no
/// <c>ExistingNode</c>, as the delivery gate asks) delegates to its main node without a landing check.
/// </summary>
public class SatelliteMainNodeLandingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SatLanding";
    private const string Doc = Partition + "/Doc";
    private const string Private = Partition + "/Private";
    private const string Elsewhere = "SatLandingElsewhere";

    /// <summary>Commenter on <see cref="Doc"/> only.</summary>
    private const string DocCommenter = "sat-landing-doc-commenter";

    /// <summary>Commenter on the whole partition; <see cref="Private"/> breaks that inheritance.</summary>
    private const string PartitionCommenter = "sat-landing-partition-commenter";

    /// <summary>Editor on <see cref="Doc"/>, no rights in <see cref="Elsewhere"/>.</summary>
    private const string DocEditor = "sat-landing-doc-editor";

    private static TimeSpan Budget => TestTimeouts.Quick;

    // ConfigureMeshBase: no blanket Public-admin grant, so each identity holds only its own grant.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = Partition, NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("doc", Partition) { Name = "doc (another node)", NodeType = "Markdown" },
                new MeshNode("Private", Partition) { Name = "Private", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(Private, new PartitionAccessPolicy { BreaksInheritance = true }),
                new MeshNode(Elsewhere) { Name = Elsewhere, NodeType = "Markdown" },
                new MeshNode("run1", Elsewhere + "/_Activity")
                {
                    Name = "run1", NodeType = "Activity", MainNode = Doc,
                },
                AssignmentNodeFactory.UserRole(DocCommenter, Role.Commenter.Id, Doc),
                AssignmentNodeFactory.UserRole(PartitionCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(DocEditor, Role.Editor.Id, Doc));

    private static AccessContext Person(string id) => new() { ObjectId = id, Name = id };

    private Task<NodeValidationResult> CreateVerdict(string who, MeshNode node, CancellationToken ct)
    {
        var rls = Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<RlsNodeValidator>().Single();
        return rls.Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create, Node = node, AccessContext = Person(who),
            })
            .FirstAsync().Timeout(Budget).Await(ct);
    }

    private static MeshNode Comment(string ns, string mainNode) =>
        new("c1", ns) { NodeType = "Comment", Name = "c1", MainNode = mainNode };

    [Fact(Timeout = 60_000)]
    public async Task ACaseVariantOfTheOwner_IsNotTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CreateVerdict(DocCommenter, Comment($"{Partition}/doc/_Comment", Doc), ct)).IsValid.Should().BeFalse(
            "'SatLanding/doc' is another node: a pointer to 'SatLanding/Doc' must not authorise a comment under it");

        (await CreateVerdict(DocCommenter, Comment($"{Doc}/_Comment", Doc), ct)).IsValid.Should().BeTrue(
            "the control: the comment's owner is the node the commenter may comment on");
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAncestorAboveAnInheritanceBreak_IsNotTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CreateVerdict(PartitionCommenter, Comment($"{Private}/_Comment", Partition), ct)).IsValid.Should().BeFalse(
            "Private breaks the partition's grant: naming the partition as main node must not reach into it");

        (await CreateVerdict(PartitionCommenter, Comment($"{Doc}/_Comment", Doc), ct)).IsValid.Should().BeTrue(
            "the control: the owner pointer inherits the partition's grant");
    }

    [Fact(Timeout = 60_000)]
    public async Task AStoredOffPartitionPointer_GovernsAnUpdateOfTheStoredNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = await Mesh.GetWorkspace().GetMeshNodeStream($"{Elsewhere}/_Activity/run1")
            .Where(n => n is not null).Select(n => n!)
            .Should().Within(TestTimeouts.Convergence).Emit("the stored satellite must load", ct);
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Activity", NodeOperation.Update);
        rule.Should().NotBeNull("Activity registers the satellite rule");

        (await rule!.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Update, Node = stored, AccessContext = Person(DocEditor),
                }, DocEditor)
            .FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeTrue("the stored pointer names Doc, where the editor holds Update; no pointer is being chosen");

        (await rule.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Update, Node = stored, AccessContext = Person(DocCommenter),
                }, DocCommenter)
            .FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeFalse("the control: a commenter holds no Update on the main node");
    }
}
