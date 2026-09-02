#pragma warning disable CS1591

using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A PERMISSION DENIAL IS AN ANSWER THE TOOL SURFACE RENDERS, NEVER AN UNHANDLED EXCEPTION.
/// Issue #3121.
///
/// <para><b>What production showed.</b> An MCP caller without write access invoked <c>recycle</c>
/// on <c>Store/Catalog</c>; the mesh refused correctly, and the refusal reached the client as
/// <c>"recycle" threw an unhandled exception — System.UnauthorizedAccessException: Access denied:
/// user 'rbuergi' lacks Update permission on 'Store/Catalog'</c>, top frame
/// <c>McpToolResult.AsToolResult</c>. The caller cannot tell "you were refused" from "the tool is
/// broken", and the server logs a stack trace for a decision it made on purpose.</para>
///
/// <para><b>Why the operation's own legible refusal never ran — the root cause, measured.</b>
/// <c>MeshOperations.Recycle</c> has carried an actionable refusal envelope since #2901, gated on
/// <c>hub.CheckPermissionOutcome(path, Update)</c>. That pre-flight was DEAD CODE on every session
/// surface. <c>HubPermissionExtensions.ResolveEvaluator</c> reads
/// <c>hub.Configuration.Get&lt;EffectivePermissionsDelegate&gt;() ?? DefaultEvaluator</c>, does not
/// walk the parent chain, and the default returns <see cref="Permission.All"/> — while a hub's
/// configuration starts EMPTY, inheriting nothing. <see cref="SessionHubFactory"/> — the ONE
/// factory behind MCP, REST, gRPC and the CLI — never copied the mesh's evaluator, so every
/// client-side check a session issued answered GRANTED, for every caller, on every path. The only
/// gate that ever fired was the OWNER's, whose <c>DeliveryFailure{Unauthorized}</c> arrives as an
/// Rx fault.
/// <see cref="ASessionsPermissionCheckAnswersTheSameAsTheMeshs"/> pins that directly;
/// <c>MeshExtensions.NodeOperationExecutionHub</c> had already been fixed for the identical reason,
/// and <c>MeshOperations.Export</c> works around it by resolving the mesh hub by hand.</para>
///
/// <para><b>And the two shapes it produced were different, which is why both are pinned.</b>
/// Measured on this tree before the fix — with a throwaway probe whose fixture is the one below,
/// under its own path names — a Viewer (<c>Read|Execute|Api</c>) calling <c>Recycle</c> through a
/// real session hub got:
/// <list type="bullet">
///   <item><description>a <b>NodeType</b> node — the stamp is a real write, the owner refuses it,
///     and <c>RecycleCore</c> re-raised: <c>THREW System.UnauthorizedAccessException: Access
///     denied: user '…-viewer' lacks Update permission on '…/Ty'</c> — the production line's
///     exception type and message template, reproduced.</description></item>
///   <item><description>a <b>non-NodeType</b> node — the stamp is an identity update that writes
///     nothing, so NOTHING was gated and the answer was
///     <c>{"status":"Recycled"}</c>: the <c>DisposeRequest</c> went out for a caller with no write
///     access at all. The 2026-08-30 guarantee that "a refused recycle no longer tears the hub
///     down" held only for NodeTypes, because the authorization was a SIDE EFFECT of the stamp
///     rather than a decision the operation made.</description></item>
/// </list></para>
///
/// <para><b>The fix has two halves and both are load-bearing.</b> The session's pre-flight becomes
/// a real gate (so a refusal is the normal, legible path and the destructive half never runs), and
/// the OWNER's verdict — which remains the authority, at a different hub and a later moment — is
/// rendered in the same envelope instead of escaping as a fault. A pre-flight can only ever be
/// check-then-act; it does not make the owner's answer optional.</para>
///
/// <para>🚨 <b>A verdict and a non-verdict are not the same answer</b> (#974, #3017), and this
/// change must not blur them: see <see cref="OnlyAVerdictReadsAsADenial"/>, which pins that only a
/// decisive refusal renders as "you lack permission" and that a hub going away or an unevaluated
/// check keeps its own, retryable answer.</para>
/// </summary>
public class SessionDenialIsAnAnswerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "SessionDenial";

    /// <summary>An ordinary node: its recycle stamp writes NOTHING, so nothing gates it.</summary>
    private const string PlainPath = Partition + "/Doc";

    /// <summary>A NodeType: its recycle stamp is a real write, and the owner refuses it.</summary>
    private const string NodeTypePath = Partition + "/Ty";

    /// <summary>
    /// A second partition, holding an <c>Activity</c> satellite whose <c>MainNode</c> points back
    /// into <see cref="Partition"/>. The split is what makes the case sharp: a satellite under its
    /// own main node inherits that node's grants by path, so the raw check and the rule agree by
    /// construction and nothing is being tested. <c>MainNode</c> is a POINTER, not a parent — the
    /// hazard <c>MainNodeRebasing</c> documents — and <c>SatelliteAccessRule</c> delegates to
    /// wherever it points.
    /// </summary>
    private const string Elsewhere = "SessionDenialElsewhere";

    /// <summary>
    /// The satellite: <c>Update</c> on its own path is DENIED for the Editor, and its registered
    /// <c>SatelliteAccessRule</c> ("a satellite's write is Update on its MainNode") GRANTS.
    /// </summary>
    private const string SatellitePath = Elsewhere + "/_Activity/run1";

    /// <summary>Read|Execute|Api — no Update anywhere in this partition.</summary>
    private const string ViewerUser = "session-viewer";

    /// <summary>
    /// Update but no Delete — the entitlement that makes the satellite case sharp: the raw check on
    /// the satellite's OWN path denies, and the node type's rule grants via the MainNode.
    /// </summary>
    private const string EditorUser = "session-editor";

    /// <summary>🚨 <see cref="TestTimeouts"/>, never a literal — CI-scaled, and it reports what it
    /// was waiting for instead of dying anonymously.</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which
    // grants Public the Admin role in every default partition — under it the Viewer below would
    // hold Update outright and every assertion here would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Partition) { Name = "Session Denial", NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = "Markdown" },
                new MeshNode("Ty", Partition) { Name = "Ty", NodeType = MeshNode.NodeTypePath },
                new MeshNode(Elsewhere) { Name = "Elsewhere", NodeType = "Markdown" },
                new MeshNode("run1", Elsewhere + "/_Activity")
                {
                    Name = "Compile run",
                    NodeType = "Activity",
                    // The satellite pointer, into the OTHER partition. Without it
                    // SatelliteAccessRule takes its degenerate branch and falls back to the plain
                    // path check — i.e. the answer with no rule at all — so this line is what makes
                    // the case a satellite whose rule DISAGREES with the raw path.
                    MainNode = PlainPath,
                },
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Partition),
                // Scoped to `Partition` ONLY: Update on `SessionDenial/Doc`, nothing at all in
                // `SessionDenialElsewhere`.
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Partition));

    /// <summary>
    /// The session hub every API surface issues on, materialised exactly as MCP materialises it —
    /// through <see cref="SessionHubFactory"/>, which is the subject here, not a stand-in for it.
    /// </summary>
    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh,
        "mcp",
        "denial",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SessionDenialIsAnAnswerTest)));

    private void ActAs(string userId) => Mesh.ServiceProvider.GetRequiredService<AccessService>()
        .SetContext(new AccessContext { ObjectId = userId, Name = userId });

    private void ActAsViewer() => ActAs(ViewerUser);

    /// <summary>
    /// 🚨 THE ROOT CAUSE, pinned as one comparison: the SAME question, asked on the session hub and
    /// on the mesh hub, must get the SAME answer.
    ///
    /// <para>Pre-fix the session hub answered <c>granted=True</c> and the mesh hub
    /// <c>granted=False</c> — a gate that cannot fail, which AGENTS.md names as its own bug class.
    /// Asserting the AGREEMENT rather than the denial is deliberate: a fix that disabled the mesh's
    /// evaluator would satisfy "both deny" and is caught by the second assertion, which pins that
    /// the shared answer is the RESTRICTIVE one.</para>
    /// </summary>
    [Fact]
    public async Task ASessionsPermissionCheckAnswersTheSameAsTheMeshs()
    {
        ActAsViewer();

        var onSession = await SessionHub().CheckPermissionOutcome(PlainPath, Permission.Update)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);
        var onMesh = await Mesh.CheckPermissionOutcome(PlainPath, ViewerUser, Permission.Update)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);

        onSession.IsGranted.Should().Be(onMesh.IsGranted,
            "a session hub is where MCP / REST / gRPC / CLI issue every operation, so a permission "
            + "check there must reach the mesh's verdict — not DefaultEvaluator's Permission.All");
        onMesh.IsGranted.Should().BeFalse(
            "a Viewer holds Read|Execute|Api and no Update — if this flips, the agreement above is "
            + "vacuous and the gate has been widened rather than wired");
    }

    /// <summary>
    /// The production line, reproduced: a NodeType whose recycle stamp is a real write the owner
    /// refuses. Pre-fix this threw <see cref="UnauthorizedAccessException"/> out of the operation,
    /// past MeshOperations, into the transport's one Task bridge.
    /// </summary>
    [Fact]
    public async Task RecyclingANodeTypeWithoutUpdateAnswersInsteadOfThrowing()
        => (await RecycleAsViewer(NodeTypePath)).Should().Be(MeshOperations.RecycleDeniedMessage,
            "a denial is something the mesh DECIDED — the operation renders it in its own envelope, "
            + "never as an unhandled exception the MCP server logs with a stack trace");

    /// <summary>
    /// The half the 2026-08-30 fix could not reach: an ordinary node, whose recycle stamp writes
    /// nothing and is therefore gated by nothing. Pre-fix this answered <c>{"status":"Recycled"}</c>
    /// and posted the <c>DisposeRequest</c> for a caller with no write access.
    ///
    /// <para>The envelope IS the assertion that nothing was performed, and deliberately so: the
    /// <c>DisposeRequest</c> is posted on exactly one code path, the one that returns
    /// <c>{"status":"Recycled"}</c>. A refusal envelope therefore proves that path was not taken.
    /// The alternative — waiting to observe that no dispose arrived — is settle-by-silence, which
    /// passes for the wrong reason on a slow mesh and reds for the wrong reason on a fast one.</para>
    /// </summary>
    [Fact]
    public async Task RecyclingAPlainNodeWithoutUpdateIsRefusedNotPerformed()
        => (await RecycleAsViewer(PlainPath)).Should().Be(MeshOperations.RecycleDeniedMessage,
            "recycling disposes the node's hub — a caller who may not write the node may not tear "
            + "it down either, and that must not depend on whether the stamp happens to be a write");

    /// <summary>
    /// 🚨 THE OTHER DIRECTION, and the regression this change had to avoid CREATING. Making the
    /// pre-flight live is only safe if it asks the question the OWNER asks — and since #3061/#3100
    /// the owner's delivery gate re-decides a denied raw check through the node's registered
    /// <c>INodeTypeAccessRule</c>. A pre-flight that stopped at the raw path would refuse an editor
    /// the owner grants: a NEW false refusal, introduced by a fix for a legibility bug.
    ///
    /// <para>The fixture is a satellite whose <c>MainNode</c> points into ANOTHER partition, which
    /// is the only shape where the two answers can differ: a satellite living UNDER its main node
    /// inherits that node's grants by path, so raw and rule agree by construction and the case
    /// would be vacuous. <c>MainNode</c> is a pointer, not a parent.</para>
    ///
    /// <para>So: the Editor holds Update in <see cref="Partition"/> and nothing in
    /// <see cref="Elsewhere"/>. Raw <c>Update</c> on the satellite's own path — DENIED.
    /// <c>SatelliteAccessRule</c> — "a satellite's write is Update on its MainNode" — GRANTED.
    /// The recycle must go through.</para>
    /// </summary>
    [Fact]
    public async Task RecyclingASatelliteFollowsItsNodeTypesRule_NotTheRawPath()
    {
        ActAs(EditorUser);

        var rawCheck = await Mesh.CheckPermissionOutcome(SatellitePath, EditorUser, Permission.Update)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);
        rawCheck.IsGranted.Should().BeFalse(
            "the Editor's grant is scoped to the other partition — if the raw path check GRANTS, "
            + "the rule is never consulted and this test proves nothing");

        var answer = await new MeshOperations(SessionHub()).Recycle(SatellitePath)
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"Recycle({SatellitePath}) → {answer}");

        using var envelope = JsonDocument.Parse(answer);
        envelope.RootElement.GetProperty("status").GetString().Should().Be("Recycled",
            "the satellite's own access rule grants what the raw path check denies, and the "
            + "pre-flight must reach the same verdict the owner's delivery gate would");
    }

    /// <summary>
    /// 🚨 ONLY A VERDICT READS AS A DENIAL. <see cref="MeshOperations.IsWriteDenial"/> is the seam
    /// that keeps "we checked; you may not" apart from "we could not check" / "this activation is
    /// going away", and it decides on the TYPED failure — never by matching a message, which drifts
    /// the moment someone rewords a banner.
    ///
    /// <para>Pinned as a unit because the production shape that needs it — the owner refusing a
    /// write the caller's own pre-flight granted — is a two-evaluator, two-moment race (a revoked
    /// assignment between the check and the write; a cross-silo fold; an identity lost across a
    /// scheduler hop). It is real and it is why the owner-side arm must stay, but it is not
    /// reproducible fast and deterministically in a single-silo mesh, so the PREDICATE is pinned
    /// directly — the same reasoning <c>PatchLandedWriteCheckTest</c> records for <c>FieldsLandedIn</c>.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteFailures))]
    public void OnlyAVerdictReadsAsADenial(string shape, Exception failure, bool isDenial)
        => MeshOperations.IsWriteDenial(failure).Should().Be(isDenial, shape);

    public static TheoryData<string, Exception, bool> WriteFailures() => new()
    {
        {
            "a decisive refusal — the ONLY thing the write path mints UnauthorizedAccessException for",
            new UnauthorizedAccessException("Access denied: user 'x' lacks Update permission on 'p'"),
            true
        },
        {
            "the same verdict nested, as a late denial dispatched through LatePatchResponseRegistry arrives",
            new InvalidOperationException("write failed", new UnauthorizedAccessException("Access denied")),
            true
        },
        {
            "the NACK itself, before UpdateRemote maps it",
            new DeliveryFailureException(Nack(ErrorType.Unauthorized, "Access denied: user 'x' lacks Update")),
            true
        },
        {
            "a hub that is GONE — transient by contract, and the caller must RE-PROBE, not go asking for rights",
            new DeliveryFailureException(Nack(ErrorType.ShuttingDown, "… is shutting down. Rejecting now")),
            false
        },
        {
            "a fold that reached NO verdict — #974's whole point: this is not a statement about the user's rights",
            new DeliveryFailureException(Nack(ErrorType.Unavailable, "Permission check unavailable — no verdict was reached")),
            false
        },
        {
            "the owner never answered within the bound",
            new TimeoutException("The operation has timed out."),
            false
        },
        {
            "an unrecognised fault — the default must never accuse",
            new InvalidOperationException("something else entirely"),
            false
        },
    };

    private static DeliveryFailure Nack(ErrorType errorType, string message)
        => new(new MessageDelivery<RawJson>
            {
                Message = new RawJson("{}"),
                Sender = new Address("test/sender"),
                Target = new Address(NodeTypePath),
            })
        {
            ErrorType = errorType,
            Message = message
        };

    /// <summary>
    /// Runs <c>Recycle</c> the way MCP runs it — as the Viewer, on a real session hub — and returns
    /// the <c>message</c> field of the JSON envelope. A THROW propagates: the whole point is that
    /// the operation answers.
    /// </summary>
    private async Task<string?> RecycleAsViewer(string path)
    {
        ActAsViewer();
        var answer = await new MeshOperations(SessionHub()).Recycle(path)
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"Recycle({path}) → {answer}");
        using var envelope = JsonDocument.Parse(answer);
        envelope.RootElement.GetProperty("status").GetString().Should().Be("Error",
            "the operation ran and produced a refusal, so its envelope must say so");
        return envelope.RootElement.GetProperty("message").GetString();
    }
}
