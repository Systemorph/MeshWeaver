using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 <b>Issue #2742 — a permission fold that terminates WITHOUT a verdict must refuse the delivery,
/// not deliver it.</b>
///
/// <para><b>The fold has three terminals, and only two were represented.</b>
/// <c>PermissionEvaluator.GetEffectivePermissions</c> is an <c>Observable.CombineLatest</c> over the
/// grant, policy, membership and gate reads of the target's scope and every ancestor scope. A leg
/// can emit, it can fault — and it can COMPLETE WITHOUT EVER EMITTING. <c>CombineLatest</c> completes
/// the instant any source completes having produced no value, so ONE silent leg empties the whole
/// fold. <see cref="HubPermissionExtensions.CheckPermissionOutcome(IMessageHub,string,string,Permission)"/>
/// classified a value and classified a fault, and passed the empty completion straight through.</para>
///
/// <para><b>Why an empty check is an ALLOW, not a refusal.</b> <c>AccessControlPipeline</c>'s decision
/// chain ends in <c>.Where(!IsGranted).Take(1).Select(…).DefaultIfEmpty()</c>, and its <c>null</c>
/// means "no check refused ⇒ every check was granted ⇒ invoke next". A check that produced NO outcome
/// is indistinguishable from one that produced <c>Granted</c>. Measured on this tree before the fix,
/// with the test below: a <c>[RequiresPermission(Read)]</c> <c>GetDataRequest</c> was DELIVERED and
/// answered normally — no <c>DeliveryFailure</c>, no log line, nothing. That is a full authorization
/// bypass, reached through <c>WithPermissionEvaluator</c>, a public embedder seam (see
/// <c>MeshExtensions</c>), and reachable from the same defect class as the hang #2742 reports: a leg
/// of the fold that produces no value.</para>
///
/// <para><b>The fix, and why it is the honest one.</b> The tri-state already has the word for "the
/// fold reached no verdict": <see cref="PermissionCheckOutcome.Undetermined"/> — the same one a fault
/// gets. It carries <c>IsGranted=false</c>, so the delivery is refused (fail CLOSED), and it is
/// reported as <see cref="ErrorType.Unavailable"/> (retryable), never "Access denied", because no
/// verdict was reached — exactly the rule #974 established. Nothing is bounded and nothing is
/// defaulted to a verdict; a terminal that carried no information now says so out loud.</para>
///
/// <para><b>Determinism.</b> The evaluator returns <see cref="Observable.Empty{T}()"/> for the one
/// path under test, so the silent terminal is produced synchronously at subscribe time and cannot
/// race a warm cached query. Ordinary hub traffic on other paths keeps a normal evaluator, so the
/// mesh boots.</para>
/// </summary>
public class PermissionFoldSilentTerminalTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The node whose fold terminates silently — the bypass under test.</summary>
    private const string SilentPath = "FoldSilent/Silent";

    /// <summary>
    /// The OVER-REACH control's node: same mesh, same pipeline, a fold that answers normally. If the
    /// fix had turned "reached a verdict" into "unavailable" it would be exactly as wrong as the bug.
    /// </summary>
    private const string HealthyPath = "FoldSilent/Healthy";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("FoldSilent") { Name = "Fold Silent" },
                new MeshNode("Silent", "FoldSilent") { Name = "Silent Fold Node" },
                new MeshNode("Healthy", "FoldSilent") { Name = "Healthy Fold Node" })
            .ConfigureDefaultNodeHub(c => c
                .WithPermissionEvaluator((_, path, _) => path == SilentPath
                    // A fold whose CombineLatest was emptied by a silent leg looks EXACTLY like
                    // this from the outside: completes, no value, no error.
                    ? Observable.Empty<Permission>()
                    : Observable.Return(Permission.All)));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 30_000)]
    public async Task AFoldThatTerminatesWithoutAVerdict_RefusesTheDelivery_AsUnavailable()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext { ObjectId = "Entitled", Name = "Entitled" });

        // GetDataRequest carries [RequiresPermission(Permission.Read)], so it goes through the fold.
        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(SilentPath)))
            .FirstAsync().ToTask();

        // 🚨 THE REGRESSION PIN. Before the fix this threw NOTHING — the request was answered
        // normally, i.e. the gate let it through having established nothing at all.
        var ex = (await act.Should().ThrowAsync<DeliveryFailureException>()).Which;

        ex.Failure.ErrorType.Should().Be(ErrorType.Unavailable,
            "a fold that reached no verdict is an availability failure, not a statement about "
            + "this user's rights — and it must never be silence, because silence is delivered");

        ex.Message.Should().NotContain("Access denied",
            "no verdict was reached, so asserting one would send an entitled user to request "
            + "permissions they may already hold");
        ex.Message.Should().Contain("no verdict",
            "the caller has to be able to tell this is unknown and retryable, not decided");
    }

    [Fact(Timeout = 30_000)]
    public async Task AHealthyFold_OnTheSameMesh_StillAnswers()
    {
        // The over-reach control. "A thing is refused" is only meaningful if the same pipeline still
        // lets a real verdict through — otherwise the pin above would pass on a gate that refuses
        // everything, which is the failure mode a fail-closed fix is most likely to introduce.
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext { ObjectId = "Entitled", Name = "Entitled" });

        await client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(HealthyPath)))
            .FirstAsync().ToTask();
    }
}
