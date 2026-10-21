using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
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
                CustomPartitionRecord());

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
