#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins issue #2477: a RECURSIVE DELETE must make the router neither end of any delivery.
///
/// <para>The cascade fans out twice per descendant — the pre-flight
/// <see cref="ValidateDeleteRequest"/> and then the bottom-up <see cref="DeleteNodeRequest"/> —
/// and both used to be issued from <c>ResolveMeshHub(hub)</c>, i.e. THE ROUTER. Every request went
/// out stamped <c>Sender = mesh/{id}</c> and every answer was addressed straight back at it, so a
/// green run logged two <c>ROUTER_TRAFFIC</c> <c>[Error]</c> lines per recursive delete (three when
/// a leaf refused) and the whole cascade's continuations ran on the routing action block. It scales
/// with SUBTREE SIZE, on a path a user triggers by deleting a Space — the prod 2026-06-11
/// starvation shape. Measured on this suite before the fix: 17 lines across four delete tests.</para>
///
/// <para>🚨 The permission half is the reason this got its own change.
/// <see cref="ValidateDeleteRequest"/> carries <c>[RequiresPermission(Delete)]</c>, so moving where
/// it is POSTED FROM is only safe because it never decided the verdict: the delivery's
/// <c>AccessContext</c> is stamped explicitly at the fan-out (the caller's for the pre-flight, the
/// System context for the commit) and <c>AccessControlPipeline.ResolveIdentity</c> reads that —
/// never the sender address. Hence the second fact below: the denial must still fire, atomically
/// and legibly, with the router still out of it. A fix that silenced the ERROR by quietly running
/// the pre-flight as the hub would pass the first fact and fail this one.</para>
///
/// <para>Each captured record is one production ERROR line, and is itself a
/// <see cref="RouterTrafficRule"/><c>.RoleOf</c> verdict the hub made over a real delivery.</para>
/// </summary>
public class RecursiveDeleteRouterTrafficTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Carol = "carol-2477";

    private readonly RouterTrafficCapture capture = new();

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>
    /// No root-level Public→Admin — the denial in the second fact must be observable. The DevLogin
    /// admin gets an explicit root grant via <see cref="SetupAccessRightsAsync"/>.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Capture the detector's ERROR lines out of the REAL logging pipeline.
            .ConfigureServices(s => s.AddLogging(l =>
                l.Services.AddSingleton<ILoggerProvider>(capture)));

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>A dedicated client hub running under Carol's (non-admin) identity.</summary>
    private IMessageHub CarolClient()
    {
        var client = GetClient();
        client.ServiceProvider.GetRequiredService<AccessService>()
            .SetHostIdentity(new AccessContext { ObjectId = Carol, Name = Carol, Roles = [] });
        return client;
    }

    private async Task CreateSubtreeAsync(string space)
    {
        await NodeFactory.CreateNode(new MeshNode(space[(space.LastIndexOf('/') + 1)..], TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("keep", space)
        { Name = "Keep", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("a", space)
        { Name = "A", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("deep", $"{space}/a")
        { Name = "Deep", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
    }

    private string Captured() =>
        string.Join("; ", capture.Records.Select(r => $"{r.MessageType} as {r.Role} ({r.Sender} → {r.Target})"));

    // ─── 1. An authorized cascade: it completes, and the router is neither end of anything ───

    [Fact(Timeout = 60_000)]
    public async Task RecursiveDelete_Authorized_MakesTheRouterNeitherEndOfAnyDelivery()
    {
        // ── The seam ────────────────────────────────────────────────────────────────────
        Mesh.NodeOperationIssuingHub().Should().BeSameAs(Mesh.NodeOperationExecutionHub()!,
            "the cascade is issued while HOLDING the router, so the seam must hop it off");

        var space = $"{TestPartition}/rtspace";
        await CreateSubtreeAsync(space);

        // ── RoleOf over a REAL delivery of the exact message the fix moves ───────────────
        // A ValidateDeleteRequest at a real node, issued the way the pre-flight now issues it.
        // It must genuinely PASS — a refusal would prove nothing about where it ran.
        var probe = await Mesh.NodeOperationIssuingHub()
            .Observe<ValidateDeleteResponse>(
                new ValidateDeleteRequest($"{space}/keep", space),
                o => o.WithTarget(new Address($"{space}/keep")))
            .Should().Within(30.Seconds()).Emit();
        probe.Message.IsValid.Should().BeTrue(
            $"the pre-flight must pass for an authorized caller; errors: {string.Join(", ", probe.Message.Errors)}");
        RouterTrafficRule.RoleOf(probe.Target?.Type, probe.Sender?.Type, probe.Message)
            .Should().BeNull("the router must be neither end of the pre-flight's response delivery");

        // ── The cascade itself, driven from a client hub (the production shape) ──────────
        var response = (await GetClient()
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(30.Seconds()).Emit()).Message;
        response.Success.Should().BeTrue(
            $"the cascade was fully authorized; a refusal would prove nothing about routing — got: {response.Error}");

        // AUTHORITATIVE: storage, below the security layer — the subtree really drained, so the
        // per-leaf fan-out below really ran and its traffic really was observed.
        var survivors = await Storage.ListDescendantPaths(space).Should().Within(30.Seconds()).Emit();
        survivors.Should().BeEmpty(
            $"an authorized recursive delete must drain the subtree; still present: {string.Join(", ", survivors)}");

        capture.Records.Should().BeEmpty(
            "a recursive delete must never make the router an end of any delivery — the pre-flight "
            + $"and the per-leaf commit are both issued off it (#2477); got: [{Captured()}]");
    }

    // ─── 2. The refusal path still refuses — and still without the router ───

    [Fact(Timeout = 60_000)]
    public async Task RecursiveDelete_DeniedOnDescendant_StillRefusesAtomically_AndTheRouterIsNeitherEnd()
    {
        var space = $"{TestPartition}/rtdenied";
        await CreateSubtreeAsync(space);
        await NodeFactory.CreateNode(new MeshNode("protected", space)
        { Name = "Protected", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("inner", $"{space}/protected")
        { Name = "Inner", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Carol: Admin on the space — explicitly DENIED on the 'protected' descendant scope.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Carol, "Admin", space))
            .Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Carol, "Admin", $"{space}/protected", denied: true))
            .Should().Within(30.Seconds()).Emit();

        // Both grants visible to the live permission fold every delivery gate reads, so the
        // delete below races nothing.
        await Mesh.GetEffectivePermissions(space, Carol)
            .Should().Within(30.Seconds()).Match(p => p.HasFlag(Permission.Delete));
        await Mesh.GetEffectivePermissions($"{space}/protected", Carol)
            .Should().Within(30.Seconds()).Match(p => !p.HasFlag(Permission.Delete));

        var response = (await CarolClient()
            .Observe(new DeleteNodeRequest(space) { Recursive = true, ConfirmWarnings = true },
                o => o.WithTarget(new Address(space)))
            .Should().Within(30.Seconds()).Emit()).Message;

        // 🚨 THE VERDICT IS UNCHANGED. Issuing the pre-flight off the router moved WHERE the
        // request is posted from, never WHOSE identity it carries: the AccessContext is stamped
        // explicitly and the leaf's [RequiresPermission(Delete)] gate reads that.
        response.Success.Should().BeFalse(
            "carol lacks Delete on a descendant, so the WHOLE recursive delete must still be refused");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.Unauthorized,
            $"the denial must still surface as Unauthorized, got: {response.Error}");
        response.Error.Should().Contain("lacks Delete permission",
            "the denial must stay legible, naming the missing permission");
        response.Error.Should().Contain($"{space}/protected",
            "the denial must still name the denied path");

        // ATOMIC: storage-authoritative — nothing was deleted before the denial.
        var options = Mesh.JsonSerializerOptions;
        (await Storage.Read(space, options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("the root must survive a denied delete");
        (await Storage.Read($"{space}/keep", options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("no sibling may be deleted before the denial is decided");
        (await Storage.Read($"{space}/protected/inner", options).Should().Within(30.Seconds()).Emit())
            .Should().NotBeNull("descendants of the denied node must survive");

        // The refusal used to cost THREE lines: the RawJson requests going out, the
        // ValidateDeleteResponse coming back, and the leaf gate's DeliveryFailure addressed at
        // mesh/{id}. All three are the same defect and all three are gone.
        capture.Records.Should().BeEmpty(
            "a REFUSED recursive delete must not make the router an end of any delivery either — "
            + $"the leaf's denial answers the issuing hub, not the router (#2477); got: [{Captured()}]");
    }
}
