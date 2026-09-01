using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE API-TOKEN CLAMP MUST SURVIVE ROUTING. Issue #2976.
///
/// <para><b>The defect.</b> <c>AccessControlPipeline</c> restored the sender's
/// <c>AccessContext</c> onto the receiving per-node hub only when it carried ROLE CLAIMS
/// (<c>delivery.AccessContext is { Roles: { Count: &gt; 0 } }</c>). The comment justified that in
/// terms of claim-based role resolution — a mechanism that no longer exists: the 2026-08-05
/// paywall fix removed claim roles from node permissions and #2974 removed their last foothold, so
/// <c>AccessContext.Roles</c> is read NOWHERE in <c>PermissionEvaluator</c>. What the evaluator
/// does read off the restored context is <c>IsApiToken</c> (the API-token clamp) and <c>IsHub</c>
/// (the hub-credential early return) — neither of which has anything to do with roles.</para>
///
/// <para>A token minted through an IdP that emits no role claims — <b>the ordinary case</b>;
/// <c>ApiToken.Roles</c> is usually empty — therefore arrived at a per-node hub with
/// <c>Roles = []</c>, the restore was skipped, <c>accessService.Context</c> stayed null on that
/// hub, <c>capturedContext</c> was null in the fold, <b>the clamp never ran</b>, and the Bearer
/// delivery was evaluated as if it were an interactive session. The exact-read path was never
/// exposed (<c>MeshNodeStreamCache.GetStreamRaw</c> captures the caller's context itself), so this
/// was reachable only through MESSAGE-ROUTED permission checks — which is what these cases drive.</para>
///
/// <para>Note the shape, because it is the reason the condition survived its rationale: the gate's
/// own INPUT decided whether the gate ran. That is the runtime cousin of the CI rule in AGENTS.md —
/// "a gate never tests its own inputs", because "did not run" and "passed" then look identical.</para>
/// </summary>
public class RoutedApiTokenClampTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The token's owner. Deliberately NOT the DevLogin harness admin.</summary>
    private const string TokenUser = "routed-token-holder";

    /// <summary>
    /// Readable by anyone in a browser, explicitly NOT reachable through the API — the exact shape
    /// an administrator writes to take API reach away (<c>api: false</c>). It is what makes the
    /// clamp OBSERVABLE from outside: with it the two identities below differ, without it a Bearer
    /// context and a browser context reach the same verdict and nothing can fail.
    /// </summary>
    private const string CappedPartition = "RoutedApiCapped";

    private const string TargetPath = CappedPartition + "/Page";

    /// <summary>
    /// 🚨 <see cref="TestTimeouts.Quick"/>, never a literal — CI-scaled, and strictly below each
    /// case's <c>[Fact(Timeout = …)]</c> so a wait that does not converge loses first and reports
    /// WHAT it was waiting for instead of xunit killing the method anonymously.
    /// </summary>
    private static TimeSpan Budget => TestTimeouts.Quick;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which
    // grants Public the Admin role in every default partition — under it every identity carries
    // Admin (hence Api) everywhere and every assertion here would pass vacuously.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(CappedPartition) { Name = "Routed Api Capped", NodeType = "Markdown" },
                new MeshNode("Page", CappedPartition) { Name = "Capped Page", NodeType = "Markdown" },
                AssignmentNodeFactory.Policy(CappedPartition,
                    new PartitionAccessPolicy { PublicRead = true, Api = false }));

    /// <summary>
    /// A Bearer principal as <c>UserContextMiddleware</c> builds one, with the claim list EMPTY —
    /// what every token minted through an IdP that emits no role claims looks like.
    /// </summary>
    private static AccessContext Token => new()
    {
        ObjectId = TokenUser,
        Name = "Routed Token Holder",
        IsApiToken = true,
    };

    /// <summary>The same person in a browser — same node, same rights, no Bearer flag.</summary>
    private static AccessContext Browser => new()
    {
        ObjectId = TokenUser,
        Name = "Routed Token Holder",
    };

    /// <summary>
    /// The read, ROUTED: a <c>[RequiresPermission(Read)]</c> message posted from a client hub at
    /// the per-node hub, carrying <paramref name="identity"/> as the delivery's AccessContext
    /// exactly as the sender's PostPipeline stamps it. Yields the gate's
    /// <see cref="DeliveryFailure"/>, or null when the delivery was SERVED.
    /// </summary>
    private IObservable<DeliveryFailure?> RoutedRead(AccessContext identity)
        => RequestHub
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(TargetPath)).WithAccessContext(identity))
            .Take(1)
            .Select(_ => (DeliveryFailure?)null)
            .Catch((DeliveryFailureException ex) => Observable.Return<DeliveryFailure?>(ex.Failure));

    /// <summary>
    /// 🚨 THE PIN. A Bearer delivery with an EMPTY claim list, message-routed to the per-node hub
    /// that owns the path, must be clamped: the administrator has said "not reachable through the
    /// API", and that decision is made from the live policy on THIS path.
    ///
    /// <para>Pre-fix this was SERVED. The restore's <c>Roles.Count &gt; 0</c> condition skipped a
    /// claimless token, so the fold snapshotted no caller context, never saw <c>IsApiToken</c>, and
    /// returned the public <c>Read</c> the same person's browser gets.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ABearerDeliveryWithNoClaims_IsClampedOnThePerNodeHub()
    {
        var failure = await RoutedRead(Token).Should().Within(Budget).Emit();

        failure.Should().NotBeNull(
            "a claimless Bearer delivery reached the gate and was SERVED — the API-token clamp "
            + "never ran, because the context that carries IsApiToken was only restored for a "
            + "context carrying role CLAIMS, which the ordinary token does not have (#2976)");
        failure!.ErrorType.Should().Be(ErrorType.Unauthorized,
            "the fold reached a verdict — the token may not use this path's API surface — so the "
            + "refusal is a denial, not an availability answer: " + failure.Message);
        failure.Message.Should().Contain(TokenUser,
            "the denial names the principal it refused, so an operator can act on it");
    }

    /// <summary>
    /// The companion measurement that makes the pin a CAPABILITY decision rather than a blanket
    /// deny: the SAME person, the SAME node, the SAME routed message — without the Bearer flag —
    /// is served. Capping <c>Api</c> withdraws the API surface, not the public page.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheSamePersonInABrowser_IsNotRefused()
    {
        var failure = await RoutedRead(Browser).Should().Within(Budget).Emit();

        failure.Should().BeNull(
            "the page is PublicRead — an interactive session reads it, and the clamp that refuses "
            + "the Bearer context above is about the API surface, nothing else: "
            + (failure?.Message ?? string.Empty));
    }
}
