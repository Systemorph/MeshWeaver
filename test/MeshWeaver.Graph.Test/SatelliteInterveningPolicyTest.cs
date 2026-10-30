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
/// 🛡️ A satellite's access is delegated to its main node, but a policy on the path BETWEEN the main
/// node and where the satellite lands still applies (stricter outcome wins). Delegating to the main
/// node alone skipped every scope in between, so an inheritance-breaking or capping policy on an
/// intermediate segment did not stop a write the main node's grant allowed. Each case carries a
/// control that differs only by that policy.
/// </summary>
public class SatelliteInterveningPolicyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SatPolicy";

    /// <summary>A container segment that breaks the partition's inheritance.</summary>
    private const string Ledger = Partition + "/_ledger";

    /// <summary>The same shape without a policy — the control for <see cref="Ledger"/>.</summary>
    private const string Open = Partition + "/_open";

    private const string Locked = Partition + "/Locked";
    private const string Doc = Partition + "/Doc";

    /// <summary>A main node whose comment container refuses the API surface (<c>Api = false</c>).</summary>
    private const string Tokened = Partition + "/Tokened";

    /// <summary>Commenter on the whole partition.</summary>
    private const string PartitionCommenter = "sat-policy-partition-commenter";

    private static TimeSpan Budget => TestTimeouts.Quick;

    // ConfigureMeshBase: no blanket Public-admin grant, so each identity holds only its own grant.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = Partition, NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("Locked", Partition) { Name = "Locked", NodeType = "Markdown" },
                new MeshNode("Tokened", Partition) { Name = "Tokened", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(Ledger, new PartitionAccessPolicy { BreaksInheritance = true }),
                // A static policy on a mapped satellite container: comments under Locked are closed.
                AssignmentNodeFactory.Policy($"{Locked}/_Comment", new PartitionAccessPolicy { Read = false }),
                AssignmentNodeFactory.Policy($"{Tokened}/_Comment", new PartitionAccessPolicy { Api = false }),
                AssignmentNodeFactory.UserRole(PartitionCommenter, Role.Commenter.Id, Partition));

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
    public async Task AnInheritanceBreakBetweenMainNodeAndLanding_RefusesTheCreate()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CreateVerdict(PartitionCommenter, Comment(Ledger, Partition), ct)).IsValid.Should().BeFalse(
            "_ledger breaks the partition's grant: Comment on the main node must not reach past it");

        (await CreateVerdict(PartitionCommenter, Comment(Open, Partition), ct)).IsValid.Should().BeTrue(
            "the control: the same write with no policy in between follows the main node");
    }

    [Fact(Timeout = 60_000)]
    public async Task ACapOnAMappedContainer_RefusesTheReadOfASatelliteInIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read);
        rule.Should().NotBeNull("Comment registers the satellite rule");

        (await ReadVerdict(rule!, Comment($"{Locked}/_Comment", Locked), ct)).Should().BeFalse(
            "Read=false on Locked/_Comment applies to the comments in it, whatever the main node grants");

        (await ReadVerdict(rule!, Comment($"{Doc}/_Comment", Doc), ct)).Should().BeTrue(
            "the control: a comment with no policy between it and its main node follows the main node");
    }

    /// <summary>
    /// The evaluator accepts a main node written as a reference (<c>@/Space/Doc</c>), so the policies
    /// in between must apply to that spelling too. A raw ancestry check skipped them for it, and the
    /// main node's grant alone admitted the read (review of #7676).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AReferenceSpelledMainNode_StillMeetsThePolicyInBetween()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await ReadVerdict(rule, Comment($"{Doc}/_Comment", "@/" + Doc), ct)).Should().BeTrue(
            "the control: the evaluator honours the @/ spelling, so the grant on the main node answers");

        (await ReadVerdict(rule, Comment($"{Locked}/_Comment", "@/" + Locked), ct)).Should().BeFalse(
            "Read=false on Locked/_Comment applies whatever spelling the stored main node uses");
    }

    /// <summary>
    /// The landing check is composed with the caller's context, like the main-node check beside it.
    /// The fold snapshots the ambient context when it is composed, so a landing check composed inside
    /// the policy stream's selector ran under the EMITTING thread's context: a token admitted at the
    /// main node passed an intervening <c>Api = false</c>. Here the verdict is composed under the
    /// token and subscribed outside it (review of #7676).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnInterveningApiFalse_RefusesAToken_WhereverTheVerdictIsSubscribed()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var token = Person(PartitionCommenter) with { IsApiToken = true };

        IObservable<bool> ComposedUnderTheToken(MeshNode comment)
        {
            using (access.SwitchAccessContext(token))
                return rule.HasAccess(new NodeValidationContext
                    {
                        Operation = NodeOperation.Read, Node = comment, AccessContext = token,
                    }, PartitionCommenter);
        }

        (await ComposedUnderTheToken(Comment($"{Doc}/_Comment", Doc)).FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeTrue("the control: the token holds Api on Doc and nothing in between caps it");

        (await ComposedUnderTheToken(Comment($"{Tokened}/_Comment", Tokened)).FirstAsync().Timeout(Budget).Await(ct))
            .Should().BeFalse("Api=false on Tokened/_Comment clamps the token there, so the read below it is refused");
    }

    // The rule is asked about the satellite as the delivery gate asks about a stored one: the node
    // itself, with its stored main node. Loading it first would read it through this very rule.
    private Task<bool> ReadVerdict(INodeTypeAccessRule rule, MeshNode comment, CancellationToken ct)
        => rule.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Read, Node = comment, AccessContext = Person(PartitionCommenter),
                }, PartitionCommenter)
            .FirstAsync().Timeout(Budget).Await(ct);
}
