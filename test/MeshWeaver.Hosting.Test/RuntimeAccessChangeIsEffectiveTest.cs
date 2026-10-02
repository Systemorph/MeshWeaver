using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A grant or a deny written at RUNTIME decides the very next permission check — including when
/// the role it grants or revokes is ALSO held through a STATIC grant.
///
/// <para>The permission fold used to emit a synchronous "fast" snapshot built from the static
/// assignments and policies alone before the enriched fold. Every one-shot consumer — the delivery
/// gate's <c>Take(1)</c>, a <c>CheckPermission</c> awaited once, the RLS node validator — took that
/// snapshot as its verdict, so a runtime Admin deny on a node whose Admin came from a static grant
/// was written, read back from storage, and ignored: <c>Delete</c> stayed <c>true</c> on every fresh
/// check (measured 36 s in the move fixture, and the long-lived fold emitted <c>[true, false]</c>).
/// The mirror image is a runtime grant layered on a static one, whose first answer was the static
/// role's, missing the runtime role entirely.</para>
///
/// <para>Each test checks the subject BEFORE the change first, which both opens the partition's
/// security query (so the change lands on a hot, already-snapshotted query — the case a stale read
/// would show) and is the control that the change is what flips the verdict.</para>
/// </summary>
public class RuntimeAccessChangeIsEffectiveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SubjectId = "runtime-access-subject";
    private static string AreaPath => $"{TestPartition}/runtimeaccess";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>Statically: Viewer on the partition, Admin on the area. Built from
    /// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> — the default mesh grants Public → Admin,
    /// under which no denial is observable.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                Assignment($"{SubjectId}_Viewer", TestPartition, Role.Viewer.Id, denied: false),
                Assignment($"{SubjectId}_Admin", AreaPath, Role.Admin.Id, denied: false));

    [Fact(Timeout = 60000)]
    public async Task ARuntimeDenyOfAStaticallyGrantedRole_DecidesTheNextCheck()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var held = await CreateNode(NewPath(AreaPath, "held"));
        var free = await CreateNode(NewPath(AreaPath, "free"));

        (await DeleteAllowed(held)).Should().BeTrue("the static Admin grant on the area gives Delete");

        await NodeFactory.CreateNode(Assignment($"{SubjectId}_NoAdmin", held, Role.Admin.Id, denied: true))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        (await DeleteAllowed(held)).Should().BeFalse(
            "the runtime deny of Admin at the held node's own scope removes the role that granted "
            + "Delete — the next check must see it, not a snapshot of the static grants alone");
        // The control: the deny is scoped to the held node, so the sibling keeps the static Admin.
        (await DeleteAllowed(free)).Should().BeTrue("the deny names only the held node's scope");
    }

    [Fact(Timeout = 60000)]
    public async Task ARuntimeGrantOnTopOfAStaticOne_DecidesTheNextCheck()
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var granted = await CreateNode(NewPath(TestPartition, "granted"));
        var other = await CreateNode(NewPath(TestPartition, "other"));

        (await DeleteAllowed(granted)).Should().BeFalse("the static Viewer grant gives no Delete");

        await NodeFactory.CreateNode(Assignment($"{SubjectId}_Admin", granted, Role.Admin.Id, denied: false))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        (await DeleteAllowed(granted)).Should().BeTrue(
            "the runtime Admin grant at the node's own scope must decide the next check, not the "
            + "static Viewer grant alone");
        // The control: the grant is scoped to its node, so the sibling still has Viewer only.
        (await DeleteAllowed(other)).Should().BeFalse("the grant names only its own node's scope");
    }

    /// <summary>A one-shot check — the FIRST emission, exactly what the delivery gate takes.</summary>
    private Task<bool> DeleteAllowed(string path) =>
        Mesh.CheckPermission(path, SubjectId, Permission.Delete)
            .Do(allowed => Output.WriteLine($"Delete on {path} for {SubjectId}: {allowed}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<string> CreateNode(string path)
    {
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = path,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    private static string NewPath(string parent, string prefix) =>
        $"{parent}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private static MeshNode Assignment(string id, string scope, string role, bool denied) =>
        new(id, $"{scope}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{SubjectId} — {(denied ? "denied " : "")}{role} on {scope}",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = SubjectId,
                DisplayName = SubjectId,
                Roles = [new RoleAssignment { Role = role, Denied = denied }],
            },
        };
}
