using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                    new PartitionAccessPolicy { BreaksInheritance = true }));

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

    /// <summary>The fold reads a policy only under the id <c>_Policy</c>; any other id is refused.</summary>
    [Fact]
    public void APolicyUnderAnotherId_IsRefused()
    {
        var result = PolicyPlacementValidator.Judge(new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = new MeshNode("restrict", TestPartition) { NodeType = "PartitionAccessPolicy", Content = new PartitionAccessPolicy { Read = false } },
        });
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain($"{TestPartition}/restrict");
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
        }).IsValid.Should().BeTrue();

    private Task<Permission> Effective(string path, CancellationToken ct) =>
        Mesh.GetEffectivePermissions(path, SubjectId)
            .Do(p => Output.WriteLine($"{path}: {p}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}
