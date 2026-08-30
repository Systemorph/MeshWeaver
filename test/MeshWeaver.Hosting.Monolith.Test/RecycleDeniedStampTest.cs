using System;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract, not a tidy-up
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>A denied write must not buy the destructive half of the operation</b> (memex,
/// 2026-08-30 17:11:21Z).
///
/// <para><c>Recycle</c> on a NodeType is two halves of ONE authorised operation: stamp a release
/// request on the node, then dispose its hub so the next activation acts on that stamp. The stamp
/// goes through the access pipeline; the dispose does not.</para>
///
/// <para>The stamp's failure handler caught <i>every</i> exception and disposed the hub anyway,
/// reasoning that a failed stamp is transient and "the hub bounce is still the caller's actual
/// ask". That is true of a timeout. It is not true of a <b>denial</b>. On memex an operator
/// without <c>Update</c> on a GitSynced module space called Recycle; the pipeline correctly
/// refused the stamp — <c>AccessControlPipeline: Access denied: user 'rbuergi' lacks Update
/// permission on 'Crm/Migration'</c> — and the handler then logged <c>disposing the hub
/// anyway</c> and tore it down. The NodeType was left with no release request to act on and its
/// watcher went silent; recovering it needed a second, also-unauthorised call.</para>
///
/// <para>So an unauthorised caller was handed the one half of the operation that changes the
/// world, having been refused the half that records why. Refusing outright leaves them exactly
/// where they started, which is the correct outcome for someone not permitted to change anything.
/// A transient stamp failure still proceeds — that distinction is what the fix makes.</para>
///
/// <para>🚨 <b>Two layers, and this test can only reach the first.</b> Recycle already PRE-CHECKS
/// Update and answers with a legible refusal — that is what this test observes, and it is worth
/// pinning because the memex operator received a settle-TIMEOUT instead and could not tell "the
/// trigger did not dispatch" from "you were refused". But memex proves the pre-check is not
/// sufficient: there the operator got PAST it and the OWNER refused the write, which is the
/// authoritative answer and arrives after the optimistic local fold has already said yes. The
/// second layer — throwing on <see cref="UnauthorizedAccessException"/> from the stamp rather
/// than disposing anyway — closes that gap, and no deterministic local repro exists for it
/// because it needs the pre-check and the owner to disagree. Recorded rather than faked: a test
/// that manufactured the disagreement would be pinning its own scaffolding.</para>
/// </summary>
public class RecycleDeniedStampTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Dana = "dana-recycle-denied";

    /// <summary>No root-level Public→Admin: a denial must be observable rather than granted away.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(30.Seconds()).Emit();
    }

    /// <summary>
    /// Dana's identity. 🚨 It is switched on the MESH's AccessService, not a client hub's:
    /// <c>MeshOperations</c> resolves <c>IWorkspace</c> from the hub it is given, and a client hub
    /// has none (constructing it against one answers "An exception was thrown while activating
    /// MeshWeaver.Data.IWorkspace" — an error that has nothing to do with permissions and would
    /// have let this test 'fail' for the wrong reason).
    /// </summary>
    private static AccessContext DanaContext => new() { ObjectId = Dana, Name = Dana, Roles = [] };

    [Fact(Timeout = 60_000)]
    public async Task Recycle_WhenTheReleaseStampIsDenied_RefusesInsteadOfDisposingTheHubAnyway()
    {
        var space = $"{TestPartition}/recyclespace";
        await NodeFactory.CreateNode(new MeshNode("recyclespace", TestPartition)
        { Name = "Space", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        // 🚨 It has to be a NODETYPE node. Recycle only stamps a release request when
        // IsNodeTypeNode(curr) holds; on anything else the Update lambda returns the node
        // unchanged, which is a NO-OP that never reaches the access pipeline — so a Markdown
        // node would exercise no denial at all and the test would pass having proved nothing.
        await NodeFactory.CreateNode(new MeshNode("SomeType", space)
        { Name = "Some Type", NodeType = MeshNode.NodeTypePath }).Should().Within(30.Seconds()).Emit();

        // Dana can READ the space (Viewer = Read | Execute | Api — no Update) — so the recycle resolves its target and reaches the stamp —
        // but is explicitly denied Update, which is exactly the memex operator's shape on a
        // GitSynced module space: allowed to look, never to write.
        await NodeFactory.CreateNode(AssignmentNodeFactory.UserRole(Dana, "Viewer", space))
            .Should().Within(30.Seconds()).Emit();

        await Mesh.GetEffectivePermissions(space, Dana)
            .Should().Within(30.Seconds()).Match(p => p.HasFlag(Permission.Read) && !p.HasFlag(Permission.Update));

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var ops = new MeshOperations(Mesh);

        // The whole operation must FAIL. Before the fix this returned a success payload, having
        // logged "disposing the hub anyway" — the destructive half, granted to a refused caller.
        // The context is switched around the SUBSCRIBE, because Recycle is cold: the identity that
        // matters is the one live when the write is issued, not when the observable was built.
        var failed = false;
        string? outcome = null;
        try
        {
            using (access.SwitchAccessContext(DanaContext))
                outcome = await ops.Recycle($"{space}/SomeType").Should().Within(45.Seconds()).Emit();
            Output.WriteLine($"Recycle returned: {outcome}");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Recycle threw: {ex.GetType().Name}: {ex.Message}");
            failed = ex is UnauthorizedAccessException || ex.InnerException is UnauthorizedAccessException;
        }

        // A structured refusal counts — what must NOT happen is a success payload. Locally the
        // PRE-CHECK catches this caller and answers with a legible reason; that is layer one and
        // it is worth pinning, because the memex operator got a settle-TIMEOUT instead of a denial
        // and could not tell "not dispatched" from "refused".
        failed = failed
                 || (outcome?.Contains("Update permission", StringComparison.OrdinalIgnoreCase) ?? false)
                 || (outcome?.Contains("denied", StringComparison.OrdinalIgnoreCase) ?? false);

        outcome.Should().NotContain("\"status\":\"Recycled\"",
            "a refused caller must never receive the success payload — that IS the destructive half");

        failed.Should().BeTrue(
            "a caller who may not WRITE the node may not RECYCLE it either — the stamp and the "
            + "dispose are two halves of one authorised operation, and proceeding after the write "
            + "was refused hands an unauthorised caller the destructive half for free");
    }
}
