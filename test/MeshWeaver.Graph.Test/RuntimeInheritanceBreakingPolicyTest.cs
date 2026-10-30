using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A <c>_Policy</c> with <see cref="PartitionAccessPolicy.BreaksInheritance"/> written at RUNTIME
/// either takes effect exactly as a statically declared one does, or is refused at the write. It is
/// never stored and ignored.
///
/// <para>The defect this pins: <c>{partition}/_Storage/_Policy</c> created through <c>CreateNode</c>
/// was stored and could be read back by path, yet the inherited partition Editor grant kept granting
/// Read on <c>{partition}/_Storage/files</c> for as long as anyone looked. The fold reads a
/// partition's policies through a CONTENT query, and that query does not return a row under a
/// configured satellite segment. The static declaration of the same node applied, because static
/// policies never pass through that query. <see cref="PolicyPlacementValidator"/> now refuses the
/// runtime placement.</para>
/// </summary>
public class RuntimeInheritanceBreakingPolicyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SubjectId = "inheritance-break-subject";
    private const string StaticStorage = "StaticBreak";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole(SubjectId, Role.Editor.Id, TestPartition),
                new MeshNode(StaticStorage) { Name = StaticStorage, NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(SubjectId, Role.Editor.Id, StaticStorage),
                AssignmentNodeFactory.Policy($"{StaticStorage}/_Storage",
                    new PartitionAccessPolicy { BreaksInheritance = true }),
                new MeshNode(CustomPartition) { Name = CustomPartition, NodeType = "Markdown" },
                CustomPartitionRecord(),
                new MeshNode(DriftPartition) { Name = DriftPartition, NodeType = "Markdown" },
                PartitionOwnership.PartitionDefinitionNode(new MeshNode(DriftPartition) { Name = DriftPartition }, "standard layout, remapped at runtime"));

    private const string CustomPartition = "LedgerSpace";

    /// <summary>The <c>Admin/Partition</c> record of a partition that maps a CUSTOM satellite segment.</summary>
    private static MeshNode CustomPartitionRecord()
    {
        var record = PartitionOwnership.PartitionDefinitionNode(new MeshNode(CustomPartition) { Name = CustomPartition }, "custom layout");
        var definition = (PartitionDefinition)record.Content!;
        return record with
        {
            Content = definition with
            {
                TableMappings = new(PartitionDefinition.DefaultSegmentTableMappings()) { ["_Ledger"] = "ledgers" },
            },
        };
    }

    [Fact]
    public void AddGraph_RegistersTheValidator()
        => Mesh.ServiceProvider.GetServices<INodeValidator>().OfType<PolicyPlacementValidator>()
            .Should().ContainSingle("a validator that is correct but unwired protects nothing");

    /// <summary>The comparison case: a STATIC declaration under the satellite segment applies.</summary>
    [Fact(Timeout = 60000)]
    public async Task StaticPolicyUnderASatelliteNamespace_CutsTheInheritedGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Effective($"{StaticStorage}/_Storage/files", ct)).HasFlag(Permission.Read).Should().BeFalse(
            "the static policy at StaticBreak/_Storage breaks inheritance");
        (await Effective(StaticStorage, ct)).HasFlag(Permission.Read).Should().BeTrue("control: the root grant stands");
    }

    /// <summary>The supported runtime placement: a policy on a main node's scope takes effect.</summary>
    [Fact(Timeout = 60000)]
    public async Task RuntimePolicyUnderAMainNode_CutsTheInheritedGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var child = $"{TestPartition}/child-{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(child) with { Name = child, NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var target = $"{child}/page";

        (await Effective(target, ct)).HasFlag(Permission.Read).Should().BeTrue("precondition: the partition Editor grant is inherited");

        await NodeFactory.CreateNode(AssignmentNodeFactory.Policy(child, new PartitionAccessPolicy { BreaksInheritance = true }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        await Mesh.GetEffectivePermissions(target, SubjectId)
            .Do(p => Output.WriteLine($"{target}: {p}"))
            .Where(p => !p.HasFlag(Permission.Read))
            .Should().Within(TestTimeouts.Convergence).Emit(
                $"a runtime _Policy at {child} breaking inheritance must cut the inherited Editor grant",
                cancellationToken: ct);
    }

    /// <summary>
    /// The defect: under a satellite segment the runtime policy used to be stored and ignored. It is
    /// now refused, and the store holds nothing.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RuntimePolicyUnderASatelliteNamespace_IsRefused_NotStoredAndIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var scope = $"{TestPartition}/_Storage";

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(AssignmentNodeFactory.Policy(scope, new PartitionAccessPolicy { BreaksInheritance = true }))
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("a policy the fold never reads must not be stored as though it applied");
        failure!.Message.Should().Contain($"{scope}/_Policy").And.Contain("satellite", "the refusal names the placement and why");

        var listed = await MeshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{scope} scope:children"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        listed.Items.Should().NotContain(n => n.Id == "_Policy", "a refused create must leave no node behind");
    }

    /// <summary>
    /// The segment match is the STORAGE router's: case-insensitive. <c>_storage</c> routes to the
    /// <c>storage</c> table on Postgres exactly as <c>_Storage</c> does.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RuntimePolicyUnderACaseVariantSatelliteSegment_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var scope = $"{TestPartition}/_storage";

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(AssignmentNodeFactory.Policy(scope, new PartitionAccessPolicy { BreaksInheritance = true }))
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("`_storage` is routed to the storage table, which the policy read never reaches");
        failure!.Message.Should().Contain($"{scope}/_Policy").And.Contain($"'{TestPartition}/_Policy'");
    }

    /// <summary>
    /// A node created under another type and RETYPED into a policy through
    /// <c>IMeshService.UpdateNode</c> meets the same rule as a create.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData("_Storage/_Policy")]
    [InlineData("restrict")]
    public async Task RetypingIntoAPolicy_ThroughUpdateNode_IsRefused(string relative)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown(relative.Replace("restrict", $"restrict-{Guid.NewGuid().ToString("N")[..8]}"), ct);

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(MeshNode.FromPath(path) with
                {
                    Name = path,
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    State = MeshNodeState.Active,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("a retype into a policy places the policy as surely as a create does");
        failure!.Message.Should().Contain(path);
        (await TypeOf(path, ct)).Should().Be("Markdown", "the refused retype must leave the node as it was");
    }

    /// <summary>The same retype through the upsert, whose update branch runs no validator chain.</summary>
    [Theory(Timeout = 120000)]
    [InlineData("_Storage/_Policy")]
    [InlineData("restrict")]
    public async Task RetypingIntoAPolicy_ThroughTheUpsert_IsRefused(string relative)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown(relative.Replace("restrict", $"restrict-{Guid.NewGuid().ToString("N")[..8]}"), ct);

        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateOrUpdateNodeRequest(MeshNode.FromPath(path) with
            {
                Name = path,
                NodeType = PartitionAccessPolicyNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new PartitionAccessPolicy { BreaksInheritance = true },
            })))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        Output.WriteLine($"upsert {path}: success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse("the upsert must judge a retype into a policy like a create");
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.ValidationFailed);
        response.Error.Should().Contain(path);
        (await TypeOf(path, ct)).Should().Be("Markdown", "the refused retype must leave the node as it was");
    }

    /// <summary>
    /// The canonical write API: a <c>GetMeshNodeStream(path).Update</c> from another hub reaches the
    /// owner as a merge patch, and a retype into a policy is judged there against the node it holds.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData("_Storage/_Policy")]
    [InlineData("restrict")]
    public async Task RetypingIntoAPolicy_ThroughTheStreamUpdate_IsRefused(string relative)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown(relative.Replace("restrict", $"restrict-{Guid.NewGuid().ToString("N")[..8]}"), ct);

        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(current => current with
                {
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("the owner must judge a retype into a policy that arrives as a patch");
        failure!.Message.Should().Contain(path);
        (await TypeOf(path, ct)).Should().Be("Markdown", "the refused retype must leave the node as it was");
    }

    /// <summary>
    /// The same retype issued on the node's OWN hub, through its own stream — the route code running
    /// on the owner takes. That write never reached the commit point, so the create-only rule was
    /// skipped. The control: an ordinary own write on the same hub lands.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task RetypingIntoAPolicy_ThroughTheOwnersOwnStream_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        (await TypeOf(path, ct)).Should().Be("Markdown", "precondition: the node's hub is active");
        var own = (Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never)
                   ?? throw new InvalidOperationException($"the owning hub of {path} is not active"))
            .GetWorkspace().GetMeshNodeStream();

        var failure = await Record.ExceptionAsync(() =>
            own.Update(current => current with
                {
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("an own write retyping the node into a policy meets the same commit point");
        failure!.Message.Should().Contain(path);
        (await TypeOf(path, ct)).Should().Be("Markdown", "the refused retype must leave the node as it was");

        var renamed = await own.Update(current => current with { Name = "renamed" })
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        renamed.Name.Should().Be("renamed", "the control: an ordinary own write lands");
    }

    /// <summary>
    /// The policy type matches the way NodeType resolution matches it: case-insensitively. A
    /// differently cased type must not skip the rule, on create or on retype.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ADifferentlyCasedPolicyType_IsJudged_OnCreateAndRetype()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        const string lower = "partitionaccesspolicy";

        var createFailure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(AssignmentNodeFactory.Policy($"{TestPartition}/_Storage",
                    new PartitionAccessPolicy { BreaksInheritance = true }) with { NodeType = lower })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));
        createFailure.Should().NotBeNull("`partitionaccesspolicy` resolves to the policy type");
        createFailure!.Message.Should().Contain($"{TestPartition}/_Storage/_Policy");

        var path = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var retypeFailure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(MeshNode.FromPath(path) with
                {
                    Name = path,
                    NodeType = lower,
                    State = MeshNodeState.Active,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));
        retypeFailure.Should().NotBeNull("a retype naming the policy type in another casing is still a retype into it");
        (await TypeOf(path, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// The EFFECTIVE layout is read from the partition's own record: its custom `_Ledger` segment
    /// routes to a satellite table, so a policy under it is refused end to end.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APolicyUnderASegmentOnlyThePartitionRecordMaps_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var scope = $"{CustomPartition}/area/_Ledger";

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(AssignmentNodeFactory.Policy(scope, new PartitionAccessPolicy { BreaksInheritance = true }))
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("the partition record maps `_Ledger` to its own table");
        failure!.Message.Should().Contain($"'{CustomPartition}/area/_Policy'");
    }

    /// <summary>
    /// The upsert that LOST its create race never read the node that won. The gate reads it, so a
    /// Markdown winner is judged as the node being retyped, not the incoming policy as its own
    /// predecessor.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheTransitionGate_ReadsTheStoredNode_WhenTheCallerDidNotReadIt()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown("_Storage/_Policy", ct);
        var incoming = AssignmentNodeFactory.Policy($"{TestPartition}/_Storage", new PartitionAccessPolicy { BreaksInheritance = true });

        var unread = await NodeTypeTransitionGate.FirstRefusal(Mesh, incoming, stored: null, TestUsers.Admin)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        unread.Should().NotBeNull("the stored winner is Markdown, so this write retypes it into a misplaced policy");
        unread!.Value.Refusal.English.Should().Contain(path);

        // Control: the incoming node passed as its own predecessor (the shape of the defect) checks nothing.
        var self = await NodeTypeTransitionGate.FirstRefusal(Mesh, incoming, stored: incoming, TestUsers.Admin)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        self.Should().BeNull();
    }

    /// <summary>
    /// A policy is create-only: a merge patch that turns a node into one is refused at the commit
    /// point even where the placement would be valid. (UpdateNode and the upsert reach the owner as
    /// this same patch, so they are refused too; a policy is created as one.)
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APatchIntoAPolicy_IsRefusedOnTheOwnersTurn_EvenAtAValidPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = await CreateMarkdown($"area-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var path = await CreateMarkdown($"{area[(TestPartition.Length + 1)..]}/_Policy", ct);

        var failure = await Record.ExceptionAsync(() =>
            Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Update(current => current with
                {
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                })
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("a patch cannot be judged for placement without a read, so it is refused outright");
        (await TypeOf(path, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// The <c>EntityReference("MeshNode", …)</c> patch route reaches the same node through another
    /// reducer, and must not skip the guard. The control proves the route itself lands a write.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APatchThroughAnEntityReference_IntoAPolicy_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var owner = new Address(path);

        var control = await RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new EntityReference(nameof(MeshNode), path.Split('/')[^1]), new RawJson("{\"name\":\"renamed\"}")),
                o => o.WithTarget(owner))
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"control: {control.Message}");
        await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is { Name: "renamed" })
            .Should().Within(TestTimeouts.Convergence).Emit("control: the entity-reference route lands a write", ct);

        (await Refused(RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new EntityReference(nameof(MeshNode), path.Split('/')[^1]),
                    new RawJson("{\"nodeType\":\"PartitionAccessPolicy\"}")),
                o => o.WithTarget(owner)), ct))
            .Should().BeTrue("the commit point judges the node the entity reference commits, whatever the reference");
        (await TypeOf(path, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// The owner applies a SPLICED string leaf to the live value, so a splice that turns
    /// <c>Markdown</c> into <c>PartitionAccessPolicy</c> is a retype into a policy, and is refused like
    /// the literal. The control proves the splice route itself rewrites the node type.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ASplicedNodeTypePatch_IntoAPolicy_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var path = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var owner = new Address(path);

        var spliced = (await RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"nodeType\":{\"$sd\":[0,8,\"Group\"]}}"))
                {
                    BaseValues = new RawJson("{\"nodeType\":\"Markdown\"}"),
                },
                o => o.WithTarget(owner))
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Message;
        (await TypeAt(path, spliced.CommittedVersion, ct)).Should().Be("Group", "control: the owner applies a spliced nodeType");

        (await Refused(RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(),
                    new RawJson("{\"nodeType\":{\"$sd\":[0,5,\"PartitionAccessPolicy\"]}}"))
                {
                    BaseValues = new RawJson("{\"nodeType\":\"Group\"}"),
                },
                o => o.WithTarget(owner)), ct))
            .Should().BeTrue("the commit point judges the value the splice produces");
        (await TypeOf(path, ct)).Should().Be("Group", "the refused splice must leave the node as it was");
    }

    /// <summary>
    /// A LITERAL with a stale base is three-way merged by the owner. With live <c>Group</c>, base
    /// <c>XGroup</c> and patch <c>XPartitionAccessPolicy</c>, the owner's disjoint edits produce
    /// <c>PartitionAccessPolicy</c>, although the literal names no policy. The commit point judges
    /// the merged result, so the retype is refused.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AStaleBaseLiteral_ThatTheOwnerMergesIntoAPolicy_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);

        // The hole, measured on the owner's own merge.
        var live = new System.Text.Json.Nodes.JsonObject { ["nodeType"] = "Group" };
        MeshWeaver.Data.Serialization.MeshNodePatchMerge.Apply(live,
            new System.Text.Json.Nodes.JsonObject { ["nodeType"] = "XPartitionAccessPolicy" },
            new System.Text.Json.Nodes.JsonObject { ["nodeType"] = "XGroup" });
        ((string?)live["nodeType"]).Should().Be("PartitionAccessPolicy", "precondition: the owner merges the disjoint edits");

        var path = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var owner = new Address(path);
        var literal = (await RequestHub
            .Observe<PatchDataResponse>(new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"nodeType\":\"Group\"}")),
                o => o.WithTarget(owner))
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Message;
        (await TypeAt(path, literal.CommittedVersion, ct)).Should().Be("Group", "control: a literal retype between non-policy types lands");

        (await Refused(RequestHub
            .Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"nodeType\":\"XPartitionAccessPolicy\"}"))
                {
                    BaseValues = new RawJson("{\"nodeType\":\"XGroup\"}"),
                },
                o => o.WithTarget(owner)), ct))
            .Should().BeTrue("the commit point judges what the merge produces, not the literal");
        (await TypeOf(path, ct)).Should().Be("Group");
    }

    /// <summary>
    /// QUEUED patches: an existing policy is retyped away by one patch and back by the next, sent
    /// back to back. Judged at delivery, the second saw a node that was still a policy and passed.
    /// At the commit point it is judged against the node the first patch left, so it is a retype
    /// into a policy and is refused.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AQueuedPatchBackIntoAPolicy_IsJudgedAgainstTheNodeTheEarlierPatchLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = await CreateMarkdown($"area-{Guid.NewGuid().ToString("N")[..8]}", ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.Policy(area, new PartitionAccessPolicy { Read = true }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var path = $"{area}/_Policy";
        (await TypeOf(path, ct)).Should().Be(PartitionAccessPolicyNodeType.NodeType, "precondition: a valid policy was created");
        var owner = new Address(path);

        var away = RequestHub
            .Observe<PatchDataResponse>(new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"nodeType\":\"Markdown\"}")),
                o => o.WithTarget(owner))
            .Take(1).Replay();
        var back = RequestHub
            .Observe<PatchDataResponse>(new PatchDataRequest(new MeshNodeReference(), new RawJson("{\"nodeType\":\"PartitionAccessPolicy\"}")),
                o => o.WithTarget(owner))
            .Take(1).Replay();
        using var awayConnection = away.Connect();
        using var backConnection = back.Connect();

        var awayAnswer = (await away.Timeout(TestTimeouts.Convergence).Await(ct)).Message;
        awayAnswer.Success.Should().BeTrue("retyping a policy AWAY is not a retype into one");
        (await Refused(back, ct)).Should().BeTrue("the second patch meets the Markdown node the first one left");
        // Read at the version the owner committed the first patch at. The response travels to the
        // client hub and the committed node to this hub's stream cache, unordered: the cache was
        // primed with the policy by the precondition read above, so a plain first read can still
        // return that pre-patch policy, which is not what the owner holds.
        (await TypeAt(path, awayAnswer.CommittedVersion, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// A <c>DataChangeRequest</c> sent to ANOTHER node's hub may carry a node whose path is not that
    /// hub's. Carrying a policy that way bypasses the target owner's guard, so it is refused. The
    /// control shows the same carrier hub accepts a write of its own node.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APolicyCarriedThroughAnotherNodesHub_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var carrier = await CreateMarkdown($"carrier-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var targetNode = await Mesh.GetWorkspace().GetMeshNodeStream(target)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([targetNode! with
                {
                    NodeType = PartitionAccessPolicyNodeType.NodeType,
                    Content = new PartitionAccessPolicy { BreaksInheritance = true },
                }]),
                o => o.WithTarget(new Address(carrier)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"carried write: {response.Message.Status} {response.Message.Log}");

        response.Message.Status.Should().NotBe(DataChangeStatus.Committed,
            "a policy for another node, carried through this hub, would never meet its own owner's guard");
        (await TypeOf(target, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// A synced write-back (a <c>DataChangeRequest</c> to the node's OWN hub) retyping a node into a
    /// policy at a VALID placement, as a full entity and as a delta. Policies are create-only, so both
    /// are refused at the commit point.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASyncedWriteBack_RetypingIntoAPolicy_AtAValidPlacement_IsRefused(bool asDelta)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = await CreateMarkdown($"area-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var path = await CreateMarkdown($"{area[(TestPartition.Length + 1)..]}/_Policy", ct);
        var current = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var retyped = current! with
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Content = new PartitionAccessPolicy { BreaksInheritance = true },
        };
        object entity = asDelta
            ? EntityDelta.Compute(nameof(MeshNode), current.Id, null, current, retyped, Mesh.JsonSerializerOptions)
            : retyped;

        var response = await RequestHub
            .Observe<DataChangeResponse>(DataChangeRequest.Update([entity]), o => o.WithTarget(new Address(path)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"write-back ({(asDelta ? "delta" : "full")}): {response.Message.Status}");

        response.Message.Status.Should().NotBe(DataChangeStatus.Committed, "a policy is create-only on every route");
        (await TypeOf(path, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// A delta that changes a policy's IDENTITY: committed, it would land as a new entry at a path
    /// the permission fold never reads. At the commit point it is a policy committed to a hub that is
    /// not its own node's, so it is refused, and nothing appears at the destination.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData("ignored-policy")]
    [InlineData("_policy")]
    public async Task AnIdentityChangingPolicyDelta_IsRefused_AndLeavesNoDestination(string newId)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = await CreateMarkdown($"area-{Guid.NewGuid().ToString("N")[..8]}", ct);
        await NodeFactory.CreateNode(AssignmentNodeFactory.Policy(area, new PartitionAccessPolicy { Read = true }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var path = $"{area}/_Policy";
        var policy = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var moved = policy! with { Id = newId };

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([EntityDelta.Compute(nameof(MeshNode), policy.Id, null, policy, moved, Mesh.JsonSerializerOptions)]),
                o => o.WithTarget(new Address(path)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"identity-changing delta: {response.Message.Status}");

        response.Message.Status.Should().NotBe(DataChangeStatus.Committed);
        var listed = await MeshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{area} scope:children"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        listed.Items.Should().NotContain(n => n.Id == newId, "the refused delta must leave no destination row");
    }

    /// <summary>
    /// A SNAPSHOT data change replaces the whole collection. A refused candidate must not take its
    /// predecessor with it: neither a retype of the existing node (same identity) nor an
    /// identity-changing delta of an existing policy may delete the node it was meant to replace.
    /// </summary>
    [Theory(Timeout = 120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedSnapshot_LeavesTheExistingNodeInPlace(bool changeIdentity)
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var area = await CreateMarkdown($"area-{Guid.NewGuid().ToString("N")[..8]}", ct);
        string path;
        object entity;
        string expectedType;
        if (changeIdentity)
        {
            await NodeFactory.CreateNode(AssignmentNodeFactory.Policy(area, new PartitionAccessPolicy { Read = true }))
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
            path = $"{area}/_Policy";
            var policy = await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            entity = EntityDelta.Compute(nameof(MeshNode), policy!.Id, null, policy, policy with { Id = "moved-policy" }, Mesh.JsonSerializerOptions);
            expectedType = PartitionAccessPolicyNodeType.NodeType;
        }
        else
        {
            path = await CreateMarkdown($"{area[(TestPartition.Length + 1)..]}/_Policy", ct);
            var current = await Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            entity = current! with
            {
                NodeType = PartitionAccessPolicyNodeType.NodeType,
                Content = new PartitionAccessPolicy { BreaksInheritance = true },
            };
            expectedType = "Markdown";
        }

        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([entity]) with { Options = new UpdateOptions { Snapshot = true } },
                o => o.WithTarget(new Address(path)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"snapshot ({(changeIdentity ? "identity change" : "retype")}): {response.Message.Status}");

        response.Message.Status.Should().NotBe(DataChangeStatus.Committed);

        // The OWNER must still hold the node. A benign patch is the instrument: its merge turn answers
        // NotFound when the owner no longer holds the entity, and lands only when it does.
        var renamed = $"still-here-{Guid.NewGuid().ToString("N")[..6]}";
        (await Refused(RequestHub.Observe<PatchDataResponse>(
                new PatchDataRequest(new MeshNodeReference(), new RawJson($"{{\"name\":\"{renamed}\"}}")),
                o => o.WithTarget(new Address(path))), ct))
            .Should().BeFalse("a refused snapshot candidate must not delete the node it was meant to replace");
        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is { } node && node.Name == renamed)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        after!.NodeType.Should().Be(expectedType);
    }

    /// <summary>
    /// A DELTA retyping a foreign node the carrier hub has accepted into its collection: the commit
    /// point resolves the delta to the entity it would commit, a policy that is not the hub's own
    /// node, and refuses it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AForeignDeltaIntoAPolicy_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        var target = await CreateMarkdown($"restrict-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var carrier = await CreateMarkdown($"carrier-{Guid.NewGuid().ToString("N")[..8]}", ct);
        var targetNode = await Mesh.GetWorkspace().GetMeshNodeStream(target)
            .Where(n => n is not null).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var accepted = await RequestHub
            .Observe<DataChangeResponse>(DataChangeRequest.Update([targetNode!]), o => o.WithTarget(new Address(carrier)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"carried Markdown: {accepted.Message.Status}");

        var retyped = targetNode! with
        {
            NodeType = PartitionAccessPolicyNodeType.NodeType,
            Content = new PartitionAccessPolicy { BreaksInheritance = true },
        };
        var response = await RequestHub
            .Observe<DataChangeResponse>(
                DataChangeRequest.Update([EntityDelta.Compute(nameof(MeshNode), targetNode.Id, null, targetNode, retyped, Mesh.JsonSerializerOptions)]),
                o => o.WithTarget(new Address(carrier)))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"carried delta: {response.Message.Status}");

        response.Message.Status.Should().NotBe(DataChangeStatus.Committed);
        (await TypeOf(target, ct)).Should().Be("Markdown");
    }

    /// <summary>
    /// The commit guard's decisions, one per shape: a retype into a policy and a policy committed to a
    /// foreign hub are refused; a legacy policy edit, a first commit (the create path's) and a
    /// non-policy write commit. (This replaces the delivery-time patch interpretation the guard
    /// used to run: a cold owner never reaches the commit point, because its merge turn waits for
    /// the loaded store.)
    /// </summary>
    [Fact]
    public void TheCommitGuard_RefusesRetypesAndForeignPolicies_AndKeepsLegacyEdits()
    {
        var options = Mesh.JsonSerializerOptions;
        var guard = new PolicyCommitGuard();
        var policy = AssignmentNodeFactory.Policy("TestData/area", new PartitionAccessPolicy());
        var markdown = policy with { NodeType = "Markdown", Content = null };

        guard.Refusal("MeshNode", markdown, policy, policy.Path, null, options)
            .Should().Contain(policy.Path, "a retype into a policy is refused: policies are create-only");
        guard.Refusal("MeshNode", null, policy, "TestData/carrier", null, options)
            .Should().Contain(policy.Path).And.Contain("TestData/carrier", "a policy committed to a foreign hub is refused");
        guard.Refusal("MeshNode", policy, policy with { Content = new PartitionAccessPolicy { Read = false } }, policy.Path, null, options)
            .Should().BeNull("a policy that already is one stays editable");
        guard.Refusal("MeshNode", null, policy, policy.Path, null, options)
            .Should().BeNull("a first commit of the hub's own node is the create path's, judged at create");
        guard.Refusal("MeshNode", policy, markdown, policy.Path, null, options)
            .Should().BeNull("retyping a policy away is not a retype into one");
    }

    /// <summary>
    /// An incomplete answer, or a record that does not read, is not "no record": neither establishes
    /// the standard layout. Existence comes from the listing, content from the record's node stream.
    /// </summary>
    [Fact]
    public void AnIncompleteOrUnreadableLayoutRead_IsNotEstablished()
    {
        var options = Mesh.JsonSerializerOptions;
        PolicyPlacementValidator.ClassifyRecord(
                new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, SnapshotIncomplete = true },
                "LedgerSpace")
            .Established.Should().BeFalse("a dropped read is not confirmed absence");
        PolicyPlacementValidator.ClassifyRecord(
                new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial, SilentProviders = ["pg"] },
                "LedgerSpace")
            .Established.Should().BeFalse("a silent provider is not confirmed absence");
        PolicyPlacementValidator.ClassifyRecord(
                new QueryResultChange<MeshNode>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = [new MeshNode("OtherSpace", "Admin/Partition") { NodeType = "Partition" }],
                },
                "LedgerSpace")
            .Established.Should().BeFalse("a listing that answers some other record is not confirmed absence");
        PolicyPlacementValidator.ClassifyRecord(
                new QueryResultChange<MeshNode>
                {
                    ChangeType = QueryChangeType.Initial,
                    Items = [new MeshNode("LedgerSpace", "Admin/Partition") { NodeType = "Partition" }],
                },
                "LedgerSpace")
            .Should().Be((true, "Admin/Partition/LedgerSpace"), "a listed record names the path whose stream is read");
        PolicyPlacementValidator.ClassifyRecordContent(
                new MeshNode("LedgerSpace", "Admin/Partition") { NodeType = "Partition", Content = "not a definition" },
                "LedgerSpace", options)
            .Established.Should().BeFalse("a record that does not read is not confirmed absence");
        PolicyPlacementValidator.ClassifyRecordContent(null, "LedgerSpace", options)
            .Established.Should().BeFalse("a listed record the stream did not serve is not confirmed absence");
        PolicyPlacementValidator.ClassifyRecord(
                new QueryResultChange<MeshNode> { ChangeType = QueryChangeType.Initial },
                "LedgerSpace")
            .Should().Be((true, (string?)null), "control: a complete answer without a record is the standard layout");
    }

    /// <summary>
    /// #7359 — deterministic: the listing answers a STALE snapshot of the record (standard mappings),
    /// the record's own stream already carries the new <c>_Drift</c> mapping. The layout must be the
    /// stream's; judging against the snapshot would accept a policy the fold never reads.
    /// </summary>
    [Fact]
    public async Task TheLayout_IsTheRecordsCurrentContent_NotTheListingsSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Mesh.JsonSerializerOptions;
        var stale = PartitionOwnership.PartitionDefinitionNode(new MeshNode("LedgerSpace") { Name = "LedgerSpace" }, "before the remap");
        var current = stale with
        {
            Content = ((PartitionDefinition)stale.Content!) with
            {
                TableMappings = new(PartitionDefinition.DefaultSegmentTableMappings()) { ["_Drift"] = "drifts" },
            },
        };
        var listing = Observable.Return(new QueryResultChange<MeshNode>
        {
            ChangeType = QueryChangeType.Initial,
            Items = [stale],
        });
        string? readPath = null;

        var layout = await PolicyPlacementValidator.ComposeLayoutRead(
                listing,
                path => { readPath = path; return Observable.Return<MeshNode?>(current); },
                "LedgerSpace", options)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        layout.Established.Should().BeTrue();
        layout.Definition!.TableMappings.Should().ContainKey("_Drift",
            "the record's CURRENT mapping decides the placement, not the listing's snapshot");
        readPath.Should().Be(stale.Path, "the record found by the listing is read through its own stream");
    }

    private const string DriftPartition = "DriftSpace";

    /// <summary>
    /// #7359 — the layout is the record's CURRENT content, never a query snapshot. The partition's
    /// record starts with the standard mappings; a System write then maps a new segment
    /// <c>_Drift</c> to its own table. A policy under <c>_Drift</c> is stored where the fold never
    /// reads it, so it must be refused — judged against the mapping the record carries NOW, not the
    /// one a listing answered before the change.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task APolicyUnderASegmentMappedAfterTheRecordWasListed_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var recordPath = $"{PartitionNodeType.Namespace}/{DriftPartition}";

        IObservable<MeshNode> remap;
        using (Access.ImpersonateAsSystem())
            remap = Mesh.GetWorkspace().GetMeshNodeStream(recordPath).Update(node => node with
            {
                Content = node.ContentAs<PartitionDefinition>(Mesh.JsonSerializerOptions)! with
                {
                    TableMappings = new(PartitionDefinition.DefaultSegmentTableMappings()) { ["_Drift"] = "drifts" },
                },
            });
        await remap.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        // Precondition: the record's own stream carries the new mapping.
        using (Access.ImpersonateAsSystem())
            await Mesh.GetWorkspace().GetMeshNodeStream(recordPath)
                .Where(n => n.ContentAs<PartitionDefinition>(Mesh.JsonSerializerOptions)?.TableMappings?.ContainsKey("_Drift") == true)
                .Should().Within(TestTimeouts.Convergence).Emit("the remap landed on the record", cancellationToken: ct);

        Access.SetCircuitContext(TestUsers.Admin);
        var scope = $"{DriftPartition}/area/_Drift";
        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(AssignmentNodeFactory.Policy(scope, new PartitionAccessPolicy { BreaksInheritance = true }))
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct));

        failure.Should().NotBeNull("the record maps `_Drift` to its own table NOW — a policy there is never read by the fold");
        failure!.Message.Should().Contain($"'{DriftPartition}/area/_Policy'");
    }

    /// <summary>The fold reads a policy only under the id <c>_Policy</c>; any other id is refused.</summary>
    [Fact]
    public void APolicyUnderAnotherId_IsRefused()
    {
        var result = PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = new MeshNode("restrict", TestPartition) { NodeType = "PartitionAccessPolicy", Content = new PartitionAccessPolicy { Read = false } },
        }, null);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"{TestPartition}/restrict");
    }

    /// <summary>
    /// The verdict reads the partition's EFFECTIVE mappings: a custom satellite segment is refused,
    /// and the suggested destination is the scope in front of it.
    /// </summary>
    [Fact]
    public void APolicyUnderACustomSatelliteSegment_IsRefused_WithTheMainNodeAsDestination()
    {
        var definition = new PartitionDefinition
        {
            Namespace = "Custom",
            TableMappings = new(PartitionDefinition.DefaultSegmentTableMappings()) { ["_Ledger"] = "ledgers" },
        };
        var result = PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = AssignmentNodeFactory.Policy("Custom/area/_ledger", new PartitionAccessPolicy { BreaksInheritance = true }),
        }, definition);
        result.IsValid.Should().BeFalse("`_Ledger` routes to its own table on this partition");
        result.ErrorMessage.Should().Contain("Custom/area/_ledger/_Policy").And.Contain("'Custom/area/_Policy'");

        PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = AssignmentNodeFactory.Policy("Custom/area/_ledger", new PartitionAccessPolicy { BreaksInheritance = true }),
        }, null).IsValid.Should().BeTrue("control: without the custom mapping the segment is content");
    }

    /// <summary>The refusal renders in the caller's language.</summary>
    [Fact]
    public void TheRefusal_IsLocalized()
    {
        var node = AssignmentNodeFactory.Policy("TestData/_Storage", new PartitionAccessPolicy { BreaksInheritance = true });
        var de = PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            AccessContext = new AccessContext { ObjectId = "u", Locale = "de" },
        }, null);
        var en = PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = node,
            AccessContext = new AccessContext { ObjectId = "u", Locale = "en" },
        }, null);
        de.ErrorMessage.Should().Contain("würde nie wirksam").And.Contain("TestData/_Storage/_Policy").And.Contain("'TestData/_Policy'");
        en.ErrorMessage.Should().Contain("would never take effect").And.Contain("'TestData/_Policy'");
    }

    /// <summary>
    /// An update of a node that is ALREADY a policy is not a placement, so a policy stored before the
    /// rule stays editable wherever it sits.
    /// </summary>
    [Fact]
    public void AnUpdateOfAnExistingPolicy_IsNotJudged()
    {
        var legacy = AssignmentNodeFactory.Policy("TestData/_Storage", new PartitionAccessPolicy { BreaksInheritance = true });
        PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = legacy with { Content = new PartitionAccessPolicy { Read = false } },
            ExistingNode = legacy,
        }, null).IsValid.Should().BeTrue();
    }

    /// <summary>The negative control for the validator: the supported placements pass.</summary>
    [Theory]
    [InlineData("TestData")]
    [InlineData("TestData/area/page")]
    [InlineData("")]
    public void APolicyOnAContentScope_Passes(string scope)
        => PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = AssignmentNodeFactory.Policy(scope, new PartitionAccessPolicy { BreaksInheritance = true }),
        }, null).IsValid.Should().BeTrue();

    /// <summary>
    /// Whether a raw <see cref="PatchDataRequest"/> was refused. The owner answers a refusal either
    /// as a delivery failure (an exception here) or as an unsuccessful <see cref="PatchDataResponse"/>.
    /// </summary>
    private static async Task<bool> Refused(IObservable<IMessageDelivery<PatchDataResponse>> response, CancellationToken ct)
    {
        try
        {
            var answer = await response.Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
            return !answer.Message.Success;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return true;
        }
    }

    private async Task<string> CreateMarkdown(string relative, CancellationToken ct)
    {
        var path = $"{TestPartition}/{relative}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with { Name = path, NodeType = "Markdown", State = MeshNodeState.Active })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        return path;
    }

    private Task<string?> TypeOf(string path, CancellationToken ct) =>
        Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Select(n => n!.NodeType)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// The node type at or past <paramref name="committedVersion"/>, the version a patch's success
    /// answer names. A patch's answer reaches the client hub and the committed node reaches the
    /// mesh hub's stream cache on unordered routes, so a cache entry primed by an earlier read can
    /// still hold the pre-patch node when the answer arrives; reading at the committed version
    /// observes what the owner holds. A missing version fails the read rather than reading the cache.
    /// </summary>
    private Task<string?> TypeAt(string path, long? committedVersion, CancellationToken ct) =>
        committedVersion is not { } version
            ? throw new InvalidOperationException($"the patch on {path} was answered without a committed version")
            : Mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null && n.Version >= version)
                .Select(n => n!.NodeType)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<Permission> Effective(string path, CancellationToken ct) =>
        Mesh.GetEffectivePermissions(path, SubjectId)
            .Do(p => Output.WriteLine($"{path}: {p}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}

/// <summary>
/// The transition gate holds every validator to ONE verdict: an empty completion is a refusal, never
/// consent. Pinned with a transition validator that completes without emitting.
/// </summary>
public class NodeTypeTransitionGateVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private sealed class SilentTransitionValidator : INodeValidator, INodeTypeTransitionValidator
    {
        public IReadOnlyCollection<NodeOperation> SupportedOperations => [NodeOperation.Update];
        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
            => Observable.Empty<NodeValidationResult>();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services =>
            services.AddScoped<INodeValidator, SilentTransitionValidator>());

    [Fact(Timeout = 120000)]
    public async Task AnEmptyCompletion_IsARefusal_NotConsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = new MeshNode("doc", TestPartition) { NodeType = "Markdown", Name = "doc" };
        var refusal = await NodeTypeTransitionGate
            .FirstRefusal(Mesh, node, stored: node with { NodeType = "Group" }, TestUsers.Admin)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);

        refusal.Should().NotBeNull("a validator that gave no verdict did not consent");
        refusal!.Value.Reason.Should().Be(NodeRejectionReason.Unavailable);
        refusal.Value.Refusal.Key.Should().Be(NodeTypeTransitionGate.GaveNoVerdictKey);
    }
}
