using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A MOVE removes its source, so it is answerable to everything a DELETE of the source is
/// answerable to. <c>HandleMoveNodeRequest</c> is copy-then-<c>DeleteMany</c> straight on storage,
/// and until it ran the source's delete pre-flight it consulted no <see cref="INodeValidator"/> at
/// all: a node whose delete every guard refused could be moved away, which removes it from where
/// it was exactly as a delete would (core #5938's in-use plan tier was the first one found).
///
/// <para>Each refusal is paired with its control in the SAME mesh: the validator refuses the
/// direct delete too (so it is armed, and the move refusal is the delete's), and an unrefused
/// sibling still moves (so the refusal is not a broken move).</para>
/// </summary>
public class MoveIsValidatedAsADeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RefusalReason = "this node is held in place";

    private readonly RefusePathsDeletionValidator refusing = new(RefusalReason);

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(refusing));

    [Fact(Timeout = 60000)]
    public async Task MovingANodeWhoseDeleteIsRefused_IsRefused_AndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await Seed("held", ct);
        var target = NewPath("held-moved");
        refusing.Refuse(source);

        // The validator is ARMED: the direct delete is refused with its reason.
        var deleteFault = await NodeFactory.DeleteNode(source)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        deleteFault.Should().NotBeNull("the validator refuses deleting the source");

        var moved = await Move(source, target, ct);
        moved.Success.Should().BeFalse("a move deletes the source, and that delete is refused");
        moved.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed);
        moved.Error.Should().Contain(RefusalReason, "the refusal carries the delete validator's own reason");

        (await StoredAt(source, ct)).Should().NotBeNull("the refused move leaves the source in place");
        (await StoredAt(target, ct)).Should().BeNull("the refusal comes before the copy creates anything");
    }

    [Fact(Timeout = 60000)]
    public async Task MovingAParentOfAnUndeletableDescendant_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = await Seed("parent", ct);
        var child = $"{source}/Child";
        await Create(child, "Child", ct);
        var target = NewPath("parent-moved");
        refusing.Refuse(child);

        var moved = await Move(source, target, ct);
        moved.Success.Should().BeFalse("the move deletes the descendant too, and that delete is refused");
        moved.Error.Should().Contain(child, "the refusal names the descendant that blocks it");
        moved.Error.Should().Contain(RefusalReason);

        (await StoredAt(child, ct)).Should().NotBeNull("the refused move leaves the whole subtree in place");
        (await StoredAt(target, ct)).Should().BeNull("and writes nothing at the target");
    }

    /// <summary>The negative control: with a validator registered and armed for ANOTHER path, a
    /// legitimate move of a subtree still relocates it whole.</summary>
    [Fact(Timeout = 60000)]
    public async Task ALegitimateMove_StillRelocatesTheSubtree()
    {
        var ct = TestContext.Current.CancellationToken;
        var unrelated = await Seed("unrelated", ct);
        refusing.Refuse(unrelated);
        var source = await Seed("free", ct);
        var child = $"{source}/Child";
        await Create(child, "Child", ct);
        var target = NewPath("free-moved");

        var moved = await Move(source, target, ct);
        moved.Success.Should().BeTrue(moved.Error ?? "nothing objects to deleting this source");

        (await StoredAt(target, ct)).Should().NotBeNull("the moved node is at the target");
        (await StoredAt($"{target}/Child", ct)).Should().NotBeNull("with its descendant");
        (await StoredAt(source, ct)).Should().BeNull("and nothing is left at the source");
        (await StoredAt(child, ct)).Should().BeNull();
    }

    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private Task<MeshNode?> StoredAt(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<string> Seed(string prefix, CancellationToken ct)
    {
        var path = NewPath(prefix);
        await Create(path, prefix, ct);
        return path;
    }

    private Task<MeshNode> Create(string path, string name, CancellationToken ct) =>
        NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
}

/// <summary>
/// 🚨 The permission half: an identity that holds Delete on the source's NAMESPACE — all that
/// <c>MoveNodePermissionAttribute</c> checks — but not on the source NODE. Before the move ran the
/// source's delete pre-flight, that namespace check was the only Delete check a move made, so this
/// identity could move away a node it could not delete.
///
/// <para>Grants: <see cref="SubjectId"/> is an Editor on <c>TestData</c> (no Delete anywhere) and an
/// Admin on <c>TestData/{area}</c> (Delete on the namespace); the held node additionally DENIES the
/// Admin role at its own scope, leaving the Editor role — read and write, no Delete. All three grants
/// are seeded statically: the permission fold reads static assignments directly, so the setup does not
/// depend on a runtime grant reaching the security query (a dynamically written deny did not, within
/// 36 s, in this fixture — measured while writing this test, not investigated here). Built from
/// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>: the default mesh grants Public → Admin,
/// under which every identity holds Delete and no refusal is observable.</para>
/// </summary>
public class MoveRequiresDeleteOnTheNodeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SubjectId = "mover-m";
    private const string Area = "movearea";

    private static readonly AccessContext SubjectContext = new() { ObjectId = SubjectId, Name = "Mover M" };

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string AreaPath => $"{TestPartition}/{Area}";
    private static string HeldPath => $"{AreaPath}/held";
    private static string FreePath => $"{AreaPath}/free";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                Grant($"{SubjectId}_Editor", TestPartition, "Editor", denied: false),
                Grant($"{SubjectId}_Admin", AreaPath, "Admin", denied: false),
                Grant($"{SubjectId}_NoAdmin", HeldPath, "Admin", denied: true));

    private static MeshNode Grant(string id, string scope, string role, bool denied) =>
        new(id, $"{scope}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"Mover M — {(denied ? "denied " : "")}{role} on {scope}",
            MainNode = scope,
            Content = new AccessAssignment
            {
                AccessObject = SubjectId,
                DisplayName = "Mover M",
                Roles = [new RoleAssignment { Role = role, Denied = denied }],
            },
        };

    [Fact(Timeout = 60000)]
    public async Task MovingANodeYouCannotDelete_IsRefused_AndOneYouCanIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        Access.SetCircuitContext(TestUsers.Admin);
        await Create(AreaPath, Area);
        var held = await Create(HeldPath, "held");
        var free = await Create(FreePath, "free");

        Access.SetCircuitContext(SubjectContext);

        // The setup measures what the test claims: Delete on the namespace, none on the held node.
        var onNamespace = await Mesh.CheckPermission(AreaPath, SubjectId, Permission.Delete)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var onHeld = await Mesh.CheckPermission(held, SubjectId, Permission.Delete)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Output.WriteLine($"Delete on {AreaPath}: {onNamespace}; on {held}: {onHeld}");
        onNamespace.Should().BeTrue("the subject passes the move's namespace check");
        onHeld.Should().BeFalse("but may not delete the held node itself");

        var heldTarget = $"{AreaPath}/held-moved-{Guid.NewGuid().ToString("N")[..8]}";
        var refused = await Move(held, heldTarget);
        refused.Success.Should().BeFalse("moving the node deletes it from where it is, which the subject may not do");
        (await StoredAt(held)).Should().NotBeNull("the refused move leaves the node in place");
        (await StoredAt(heldTarget)).Should().BeNull("and writes nothing at the target");

        // The control: the same identity, the same namespace, a node it MAY delete.
        var freeTarget = $"{AreaPath}/free-moved-{Guid.NewGuid().ToString("N")[..8]}";
        var moved = await Move(free, freeTarget);
        moved.Success.Should().BeTrue(moved.Error ?? "the subject may delete this node, so it may move it");
        (await StoredAt(freeTarget)).Should().NotBeNull();
        (await StoredAt(free)).Should().BeNull();
    }

    private Task<MoveNodeResponse> Move(string source, string target) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Do(r => Output.WriteLine($"MOVE {source} -> {target}: success={r.Success} reason={r.RejectionReason} error={r.Error}"))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshNode?> StoredAt(string path) =>
        Storage.Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    private async Task<string> Create(string path, string name)
    {
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }
}

/// <summary>
/// Refuses the delete of the paths it is told to, accepts everything else. Instance state owned by
/// the test's mesh, never static.
/// </summary>
internal sealed class RefusePathsDeletionValidator(string reason) : INodeValidator
{
    private System.Collections.Immutable.ImmutableHashSet<string> refused =
        System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refuse deleting <paramref name="path"/> from now on.</summary>
    public void Refuse(string path) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref refused, s => s.Add(path));

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        => Observable.Return(refused.Contains(context.Node.Path)
            ? NodeValidationResult.Invalid(reason)
            : NodeValidationResult.Valid());
}
