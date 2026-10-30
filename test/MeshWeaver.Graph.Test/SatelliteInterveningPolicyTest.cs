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
/// 🛡️ A satellite's access is delegated to its main node, but a policy or a denied role on the path
/// BETWEEN the main node and where the satellite lands still applies (stricter outcome wins).
/// Delegating to the main node alone skipped every scope in between, so an inheritance-breaking or
/// capping policy, or a role denied on an intermediate segment, did not stop a write the main node's
/// grant allowed. Each case carries a control that differs only by that policy or denial.
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

    /// <summary>Commenter on the whole partition.</summary>
    private const string PartitionCommenter = "sat-policy-partition-commenter";

    /// <summary>A main node whose comment container denies <see cref="DeniedCommenter"/> the Commenter role.</summary>
    private const string Sealed = Partition + "/Sealed";

    /// <summary>The same shape without a denial — the control for <see cref="Sealed"/>.</summary>
    private const string Unsealed = Partition + "/Unsealed";

    /// <summary>Commenter on the whole partition, with that role denied on <c>Sealed/_Comment</c>: a
    /// segment strictly between the comment's owner (its main node) and where it lands.</summary>
    private const string DeniedCommenter = "sat-policy-denied-commenter";

    private static TimeSpan Budget => TestTimeouts.Quick;

    // ConfigureMeshBase: no blanket Public-admin grant, so each identity holds only its own grant.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = Partition, NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("Locked", Partition) { Name = "Locked", NodeType = "Markdown" },
                new MeshNode("Sealed", Partition) { Name = "Sealed", NodeType = "Markdown" },
                new MeshNode("Unsealed", Partition) { Name = "Unsealed", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(Ledger, new PartitionAccessPolicy { BreaksInheritance = true }),
                // A static policy on a mapped satellite container: comments under Locked are closed.
                AssignmentNodeFactory.Policy($"{Locked}/_Comment", new PartitionAccessPolicy { Read = false }),
                AssignmentNodeFactory.UserRole(PartitionCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(DeniedCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(DeniedCommenter, Role.Commenter.Id, $"{Sealed}/_Comment", denied: true));

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
    public async Task ARoleDeniedBetweenMainNodeAndLanding_RefusesTheCreate()
    {
        var ct = TestContext.Current.CancellationToken;
        (await CreateVerdict(DeniedCommenter, Comment($"{Sealed}/_Comment", Sealed), ct)).IsValid.Should().BeFalse(
            "Commenter is denied on Sealed/_Comment: Comment on the main node must not reach past that denial");

        (await CreateVerdict(DeniedCommenter, Comment($"{Unsealed}/_Comment", Unsealed), ct)).IsValid.Should().BeTrue(
            "the control: the same writer, the same write, on a segment with no denial follows the main node");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARoleDeniedBetweenMainNodeAndSatellite_RefusesTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read);
        rule.Should().NotBeNull("Comment registers the satellite rule");

        (await ReadVerdict(rule!, Comment($"{Sealed}/_Comment", Sealed), ct, DeniedCommenter)).Should().BeFalse(
            "Commenter is denied on Sealed/_Comment: a comment stored in it is not readable through the main node");

        (await ReadVerdict(rule!, Comment($"{Unsealed}/_Comment", Unsealed), ct, DeniedCommenter)).Should().BeTrue(
            "the control: a comment beneath a segment with no denial follows the main node");
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

    // The rule is asked about the satellite as the delivery gate asks about a stored one: the node
    // itself, with its stored main node. Loading it first would read it through this very rule.
    private Task<bool> ReadVerdict(
        INodeTypeAccessRule rule, MeshNode comment, CancellationToken ct, string who = PartitionCommenter)
        => rule.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Read, Node = comment, AccessContext = Person(who),
                }, who)
            .FirstAsync().Timeout(Budget).Await(ct);
}
