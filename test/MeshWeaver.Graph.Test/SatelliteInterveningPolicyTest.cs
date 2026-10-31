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

    /// <summary>A main node whose comment container refuses the API surface (<c>Api = false</c>).</summary>
    private const string Tokened = Partition + "/Tokened";

    /// <summary>Commenter on the whole partition.</summary>
    private const string PartitionCommenter = "sat-policy-partition-commenter";

    /// <summary>A main node whose comment container denies <see cref="DeniedCommenter"/> the Commenter role.</summary>
    private const string Sealed = Partition + "/Sealed";

    /// <summary>The same shape without a denial — the control for <see cref="Sealed"/>.</summary>
    private const string Unsealed = Partition + "/Unsealed";

    /// <summary>Commenter on the whole partition, with that role denied on <c>Sealed/_Comment</c>: a
    /// segment strictly between the comment's owner (its main node) and where it lands.</summary>
    private const string DeniedCommenter = "sat-policy-denied-commenter";

    /// <summary>Commenter on the whole partition, with that role denied only OUTSIDE the range between
    /// <see cref="Unsealed"/> and its comment <c>c1</c>: on a sibling comment beneath the same container
    /// and on another main node's container. Neither denial reaches <c>Unsealed/_Comment/c1</c>.</summary>
    private const string SidewaysDeniedCommenter = "sat-policy-sideways-denied-commenter";

    /// <summary>A second partition where only <see cref="WellKnownUsers.Public"/> holds a grant.</summary>
    private const string PublicPartition = "SatPolicyPublic";

    /// <summary>A main node whose comment container denies PUBLIC the Commenter role.</summary>
    private const string PublicSealed = PublicPartition + "/Sealed";

    /// <summary>The same shape without a denial — the control for <see cref="PublicSealed"/>.</summary>
    private const string PublicUnsealed = PublicPartition + "/Unsealed";

    /// <summary>A signed-in identity with no grant of its own: everything it holds comes from Public.</summary>
    private const string Nobody = "sat-policy-nobody";

    /// <summary>A partition with NO role grant at all: its only public surface is a type gate's cover.</summary>
    private const string CoverPartition = "SatPolicyCover";

    /// <summary>A node type whose gate opens exactly the gated node itself (<see cref="NodeTypeGate.Self"/>).</summary>
    private const string CoverType = "SatPolicyCoverType";

    /// <summary>A cover whose comment container denies Public the Editor role — a role Public holds nowhere.</summary>
    private const string DeniedUnheldCover = CoverPartition + "/Denied";

    /// <summary>The same cover shape without a denial — the control for <see cref="DeniedUnheldCover"/>.</summary>
    private const string PlainCover = CoverPartition + "/Plain";

    /// <summary>A partition where Public holds Editor at the root, and each cover in it breaks inheritance.</summary>
    private const string BrokenPartition = "SatPolicyBroken";

    /// <summary>A cover that breaks inheritance and whose comment container denies Public the Editor
    /// role — a role the break has already cleared, so Public no longer holds it there.</summary>
    private const string BrokenDeniedCover = BrokenPartition + "/Denied";

    /// <summary>The same broken cover without a denial — the control for <see cref="BrokenDeniedCover"/>.</summary>
    private const string BrokenPlainCover = BrokenPartition + "/Plain";

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
                new MeshNode("Tokened", Partition) { Name = "Tokened", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(Ledger, new PartitionAccessPolicy { BreaksInheritance = true }),
                // A static policy on a mapped satellite container: comments under Locked are closed.
                AssignmentNodeFactory.Policy($"{Locked}/_Comment", new PartitionAccessPolicy { Read = false }),
                AssignmentNodeFactory.Policy($"{Tokened}/_Comment", new PartitionAccessPolicy { Api = false }),
                AssignmentNodeFactory.UserRole(PartitionCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(DeniedCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(DeniedCommenter, Role.Commenter.Id, $"{Sealed}/_Comment", denied: true),
                AssignmentNodeFactory.UserRole(SidewaysDeniedCommenter, Role.Commenter.Id, Partition),
                AssignmentNodeFactory.UserRole(SidewaysDeniedCommenter, Role.Commenter.Id, $"{Unsealed}/_Comment/c2", denied: true),
                AssignmentNodeFactory.UserRole(SidewaysDeniedCommenter, Role.Commenter.Id, $"{Doc}/_Comment", denied: true),
                new MeshNode(PublicPartition) { Name = PublicPartition, NodeType = "Markdown" },
                new MeshNode("Sealed", PublicPartition) { Name = "Sealed", NodeType = "Markdown" },
                new MeshNode("Unsealed", PublicPartition) { Name = "Unsealed", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, Role.Commenter.Id, PublicPartition),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, Role.Commenter.Id, $"{PublicSealed}/_Comment", denied: true),
                new MeshNode(CoverPartition) { Name = CoverPartition, NodeType = "Markdown" },
                new MeshNode("Denied", CoverPartition) { Name = "Denied", NodeType = CoverType },
                new MeshNode("Plain", CoverPartition) { Name = "Plain", NodeType = CoverType },
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, Role.Editor.Id, $"{DeniedUnheldCover}/_Comment", denied: true),
                new MeshNode(BrokenPartition) { Name = BrokenPartition, NodeType = "Markdown" },
                new MeshNode("Denied", BrokenPartition) { Name = "Denied", NodeType = CoverType },
                new MeshNode("Plain", BrokenPartition) { Name = "Plain", NodeType = CoverType },
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, Role.Editor.Id, BrokenPartition),
                AssignmentNodeFactory.Policy(BrokenDeniedCover, new PartitionAccessPolicy { BreaksInheritance = true }),
                AssignmentNodeFactory.Policy(BrokenPlainCover, new PartitionAccessPolicy { BreaksInheritance = true }),
                AssignmentNodeFactory.UserRole(WellKnownUsers.Public, Role.Editor.Id, $"{BrokenDeniedCover}/_Comment", denied: true))
            .ConfigureNodeTypeAccess(access => access.WithGate(CoverType, NodeTypeGate.Self));

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

    /// <summary>
    /// The negative control for the denial check: a role denied to the caller OUTSIDE the scopes
    /// between the main node and the landing (on a sibling comment beneath the same container, and on
    /// another main node's container) does not change the outcome — the create and the read through
    /// the main node are still allowed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARoleDeniedOutsideTheRangeBetweenMainNodeAndLanding_ChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await CreateVerdict(SidewaysDeniedCommenter, Comment($"{Unsealed}/_Comment", Unsealed), ct)).IsValid.Should().BeTrue(
            "the denials sit on Unsealed/_Comment/c2 and Doc/_Comment, neither of which is on the path to c1");
        (await ReadVerdict(rule, Comment($"{Unsealed}/_Comment", Unsealed), ct, SidewaysDeniedCommenter)).Should().BeTrue(
            "the same read follows the main node: no denial lies between Unsealed and Unsealed/_Comment/c1");

        (await ReadVerdict(rule, Comment($"{Doc}/_Comment", Doc), ct, SidewaysDeniedCommenter)).Should().BeFalse(
            "the positive check on the same identity: its denial on Doc/_Comment does refuse a comment stored there");
    }

    /// <summary>
    /// A denial removes only roles the subject HOLDS where it sits. Here nobody holds any role in the
    /// partition: the cover's Read comes from its type gate (<see cref="NodeTypeGate.Self"/>), which
    /// opens the gated node alone and reaches its comments by delegation. Denying Public the Editor
    /// role on the cover's <c>_Comment</c> container removes nothing, so it must not force the landing
    /// check — which folds the comment's own path, where the cover does not apply, and would refuse a
    /// read the denial never touched (review of #7723).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADenialOfAnUnheldRole_LeavesTheCoversCommentsReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await ReadVerdict(rule, Comment($"{PlainCover}/_Comment", PlainCover), ct, Nobody)).Should().BeTrue(
            "the control: the cover's gate grants Read on the main node, and its comment follows it");

        (await ReadVerdict(rule, Comment($"{DeniedUnheldCover}/_Comment", DeniedUnheldCover), ct, Nobody)).Should().BeTrue(
            "Public holds no Editor role anywhere here, so denying it on the comment container removes nothing");
    }

    /// <summary>
    /// Whether a role is still HELD where a denial sits is the fold's answer, inheritance breaks
    /// included. Public holds Editor on the partition, the cover breaks inheritance (clearing it), and
    /// Editor is denied on the cover's <c>_Comment</c> container. The break sits AT the main node, so
    /// it is outside the scopes in between and the policy check does not fire; the denial removes a
    /// role that is no longer held, so it must not force the landing check either. The cover's gate
    /// keeps its comments readable (review of #7723, thread 4431443570).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADenialOfARoleClearedByABreakAtTheMainNode_LeavesTheCoversCommentsReadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await ReadVerdict(rule, Comment($"{BrokenPlainCover}/_Comment", BrokenPlainCover), ct, Nobody)).Should().BeTrue(
            "the control: past the break only the cover's gate answers, and its comment follows it");

        (await ReadVerdict(rule, Comment($"{BrokenDeniedCover}/_Comment", BrokenDeniedCover), ct, Nobody)).Should().BeTrue(
            "the break at the cover cleared Public's Editor, so denying it on the comment container removes nothing");
    }

    /// <summary>
    /// The fold drops empty path segments, so a stored main node spelled <c>SatPolicy//Sealed</c>
    /// still inherits the partition's grant. The ancestry test compared the raw spelling, found the
    /// landing not beneath it, and skipped the scopes in between — admitting a read across the
    /// denial on <c>Sealed/_Comment</c> (review of #7688). Both paths are now normalised with the
    /// fold's own segment rules.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADoubledSlashMainNode_StillMeetsTheDenialInBetween()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await ReadVerdict(rule, Comment($"{Unsealed}/_Comment", Partition + "//Unsealed"), ct, DeniedCommenter)).Should().BeTrue(
            "the control: the fold accepts the doubled slash, so the partition grant answers through the main node");

        (await ReadVerdict(rule, Comment($"{Sealed}/_Comment", Partition + "//Sealed"), ct, DeniedCommenter)).Should().BeFalse(
            "Commenter is denied on Sealed/_Comment whatever spelling the stored main node uses");
        (await ReadVerdict(rule, Comment($"{Locked}/_Comment", Partition + "//Locked/"), ct)).Should().BeFalse(
            "Read=false on Locked/_Comment applies to a doubled- and trailing-slash spelling of the main node too");
    }

    /// <summary>
    /// A signed-in caller's fold ORs in the <see cref="WellKnownUsers.Public"/> leg, so a role denied
    /// to Public on a segment between the main node and the landing narrows what the caller holds
    /// there just as a denial of the caller's own role does. The denial detector looked only at the
    /// caller and its groups, so a caller whose only grant came from Public passed the satellite
    /// checks through the main node (review of #7688).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARoleDeniedToPublicBetweenMainNodeAndLanding_RefusesTheCreateAndTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = NodeTypeAccessRuleGate.Find(Mesh, "Comment", NodeOperation.Read)!;

        (await CreateVerdict(Nobody, Comment($"{PublicUnsealed}/_Comment", PublicUnsealed), ct)).IsValid.Should().BeTrue(
            "the control: Public's Commenter on the partition reaches a signed-in caller with no grant of its own");
        (await ReadVerdict(rule, Comment($"{PublicUnsealed}/_Comment", PublicUnsealed), ct, Nobody)).Should().BeTrue(
            "the control: the same read beneath a segment with no denial follows the main node");

        (await CreateVerdict(Nobody, Comment($"{PublicSealed}/_Comment", PublicSealed), ct)).IsValid.Should().BeFalse(
            "Commenter is denied to Public on Sealed/_Comment: the main node's inherited Public grant must not reach past it");
        (await ReadVerdict(rule, Comment($"{PublicSealed}/_Comment", PublicSealed), ct, Nobody)).Should().BeFalse(
            "a comment stored beneath Public's denial is not readable through the main node");
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
    private Task<bool> ReadVerdict(
        INodeTypeAccessRule rule, MeshNode comment, CancellationToken ct, string who = PartitionCommenter)
        => rule.HasAccess(new NodeValidationContext
                {
                    Operation = NodeOperation.Read, Node = comment, AccessContext = Person(who),
                }, who)
            .FirstAsync().Timeout(Budget).Await(ct);
}
