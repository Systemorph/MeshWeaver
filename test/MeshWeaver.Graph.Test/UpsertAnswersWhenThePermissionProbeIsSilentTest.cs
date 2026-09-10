using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3674 — an upsert whose reply hangs off a source that COMPLETES EMPTY answers nobody, and
/// the caller waits out its entire budget.</b>
///
/// <para><b>What #3674 reported</b> is <c>CreateOrUpdateNodeRequest … ⇒ the delivery reached a hub
/// but no handler</c>, then <c>ADVANCE_WITHOUT_HANDOFF … the owner never acknowledged this write</c>
/// and <c>VERDICT_TIMEOUT … bound=31s</c> — a write that is neither applied nor refused. Its own
/// thread later established that the routing reading was wrong (the <c>@</c> in a request-fate entry
/// names the hub that RECORDED the stage, not the delivery's target, so two of the three "not mine"
/// verdicts were correct forwards). What survives every re-reading is the SHAPE: the caller of a
/// sanctioned lifecycle request got no terminal at all.</para>
///
/// <para><b>This test pins the one instance of that shape that is drivable with real machinery.</b>
/// <c>HandleCreateOrUpdateNodeRequest</c> returns <c>Processed()</c> immediately and owes its reply
/// from detached chains. Its no-op branch — the branch a RE-INSTALL takes, which is exactly what CD
/// 7950's <c>[FAIL] Chess … idempotence: re-install failed</c> was doing — asks
/// <c>hub.GetEffectivePermissions(path, requestedBy)</c> whether the caller could have written
/// anyway. That probe <b>terminating without ever emitting is a known, tracked reality</b>: it is
/// half of what <c>HubPermissionExtensions.CheckPermissionOutcome</c> classifies as
/// <c>Undetermined</c>, <i>"because it FAULTED, or because it terminated without ever emitting
/// (issue #2742)"</i>. Against a two-arm <c>Subscribe</c> that is SILENCE — neither
/// <c>PostOk</c> nor the write path runs, no fault is logged, and the delivery trail simply stops.
/// </para>
///
/// <para><b>The lever is the framework's own extension point, not a mock</b> —
/// <c>MessageHubPermissionExtensions.WithPermissionEvaluator</c>, the documented
/// "inject a custom evaluator (for tests / non-standard evaluators)" seam, the same one
/// <c>MeshReadHub</c> and <c>SessionHubFactory</c> use in production. The evaluator here is
/// <b>narrowed to one user id</b> and delegates everything else to the real
/// <c>PermissionEvaluator</c>, so the create, the access-control pipeline and the cache's own write
/// gate (which probes for the CALLER's identity, not <see cref="SilentProbeUser"/>) all run
/// unchanged. Nothing is faked; one identity's answer is empty instead of a verdict.</para>
///
/// <para><b>What the fix does with silence.</b> The same thing the probe's FAULT arm has always
/// done: fall through to the ordinary write path and let the owner stay the single authority on
/// allow/deny. "No verdict" is not "denied" and it is certainly not "granted" — it is simply not an
/// answer this optimisation may act on, so the optimisation steps aside.</para>
/// </summary>
public class UpsertAnswersWhenThePermissionProbeIsSilentTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// The one identity whose effective-permission probe answers with an empty completion. Narrow
    /// on purpose: every other identity — including the one the request itself carries — keeps the
    /// real evaluator, so this test changes exactly one observable fact about the mesh.
    /// </summary>
    private const string SilentProbeUser = "3674-silent-probe-user";

    private const string NodeId = "upsert-silent-probe";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Applied AFTER AddRowLevelSecurity so this delegate is the one the mesh hub carries —
            // and the node-CRUD hub COPIES the mesh hub's delegate on creation (MeshExtensions'
            // "INHERIT THE ROUTER'S PERMISSION EVALUATOR"), which is what puts it in front of the
            // upsert handler running there.
            .ConfigureHub(c => c.WithPermissionEvaluator(
                (hub, nodePath, userId) => userId == SilentProbeUser
                    ? Observable.Empty<Permission>()
                    : PermissionEvaluator.GetEffectivePermissions(hub, nodePath, userId)));

    /// <summary>
    /// A no-op upsert (identical to the persisted node, so the no-op probe branch is taken) whose
    /// permission probe completes without a verdict. The caller MUST be answered.
    /// </summary>
    [Fact]
    public async Task NoOpUpsert_WhenThePermissionProbeCompletesWithoutAVerdict_StillAnswersTheCaller()
    {
        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        // Byte-identical to what was just created — Name, NodeType and a null Content — so
        // IsNoOpUpsert is true and the handler takes SkipNoOpIfAuthorized. RequestedBy names the
        // one identity whose probe is silent; the delivery's own AccessContext is untouched, which
        // is what keeps the write path (and its access gate) working once the fix falls through
        // to it.
        var answer = await ObserveNodeOperation(
                new CreateOrUpdateNodeRequest(
                    new MeshNode(NodeId, TestPartition) { Name = "initial", NodeType = "Markdown" })
                {
                    RequestedBy = SilentProbeUser,
                })
            .Should().Within(TestTimeouts.Convergence).Emit(
                "a CreateOrUpdateNodeRequest whose no-op permission probe completes without a "
                + "verdict must still receive a terminal — a two-arm Subscribe posts nothing at "
                + "all here, and the caller waits out its whole budget for a reply that is never "
                + "coming (MeshWeaver#3674)");

        // WHICH terminal is deliberately not over-specified: the fix hands silence to the ordinary
        // write path, so the owner decides. What must never happen again is no terminal.
        answer.Message.Should().NotBeNull();
    }
}
