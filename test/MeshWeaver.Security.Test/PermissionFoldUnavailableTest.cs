using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the permission fold's UNDETERMINED leg — a fold that FAULTS must answer
/// <see cref="ErrorType.Unavailable"/>, never "Access denied" (issue #974, split out of #637).
///
/// <para><b>The bug.</b> <c>AccessControlPipeline</c> wrapped each check in
/// <c>.Catch&lt;bool&gt;(_ =&gt; Observable.Return(false))</c> and turned every refusal into
/// <c>DeliveryFailure{ErrorType.Unauthorized}</c> reading <c>"Access denied: user 'x' lacks Read
/// permission on 'y'"</c>. When the fold faulted — a wedged access cache, a Postgres blackout, a
/// hub mid-disposal — that sentence was FALSE, and it is actionable-looking: it sends a correctly-
/// entitled user to ask an admin for permissions they already hold, which is a support ticket that
/// cannot be resolved because nothing is actually wrong with their grants.</para>
///
/// <para><b>Fail-closed is not the question.</b> Both legs refuse the delivery and neither invokes
/// the downstream handler — that is unchanged and pinned below. The only thing that changes is what
/// the caller is TOLD, and therefore what they do next: retry, or go chase a permission they
/// already have.</para>
///
/// <para><b>Determinism.</b> The fold is driven by an evaluator that throws, not by a small budget.
/// A near-zero timeout races a warm cached query that can emit synchronously during Subscribe, so
/// the value wins and the test passes for the wrong reason (that shape passed pre-merge and failed
/// on main — #976). A throwing evaluator cannot emit a value, so there is no race.</para>
/// </summary>
public class PermissionFoldUnavailableTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "FoldFault/Target";

    /// <summary>
    /// The fault this test induces: the evaluator the hub is configured with throws instead of
    /// producing a permission set. This is what a wedged access cache, an unreachable Postgres or
    /// a disposing hub look like from inside the fold.
    /// </summary>
    private sealed class FoldFaultException() : Exception("access assignment store unreachable");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("FoldFault") { Name = "Fold Fault" },
                new MeshNode("Target", "FoldFault") { Name = "Target Node" })
            .ConfigureDefaultNodeHub(c => c
                .WithPermissionEvaluator((_, _, _) =>
                    Observable.Throw<Permission>(new FoldFaultException())));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 15000)]
    public async Task AFaultedFold_AnswersUnavailable_NotAccessDenied()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Entitled", Name = "Entitled" });

        // GetDataRequest carries [RequiresPermission(Permission.Read)], so it goes through the fold.
        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(NodePath)))
            .FirstAsync().ToTask();

        var ex = (await act.Should().ThrowAsync<DeliveryFailureException>()).Which;

        // 🚨 The regression pin. Before the fix this was ErrorType.Unauthorized with a message
        // asserting the user lacked a permission — a fact the fold never established.
        ex.Failure.ErrorType.Should().Be(ErrorType.Unavailable,
            "a fold that FAULTED reached no verdict, so the answer is an availability failure, "
            + "not a statement about this user's rights");

        ex.Message.Should().NotContain("Access denied",
            "that phrase asserts a verdict the fold never produced, and it is what sends an "
            + "entitled user to request permissions they already hold");
        ex.Message.Should().Contain("no verdict",
            "the caller has to be able to tell this is unknown, not decided");
        // The operator-facing cause survives to the caller — classification, not a swallow.
        ex.Message.Should().Contain(nameof(FoldFaultException));
    }

    [Fact(Timeout = 15000)]
    public async Task AFaultedFold_StillRefusesTheDelivery()
    {
        // The other half: naming the condition honestly must NOT open the gate. An undetermined
        // fold carries IsGranted=false and the downstream handler is never invoked — if this
        // regressed, an unreachable access store would become a portal-wide authorization bypass,
        // which is a far worse bug than the one being fixed.
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Entitled", Name = "Entitled" });

        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(NodePath)))
            .FirstAsync().ToTask();

        // A DeliveryFailure — never data. The request does not succeed just because the fold broke.
        await act.Should().ThrowAsync<DeliveryFailureException>();
    }
}

/// <summary>
/// The over-reach guard for the same change: with a HEALTHY fold, a genuine denial must still read
/// as a denial (<see cref="ErrorType.Unauthorized"/> + "Access denied"). A fix that turned every
/// refusal into "unavailable" would be exactly as wrong as the bug — it would tell users to retry
/// forever instead of asking for access they really do lack.
/// </summary>
public class PermissionFoldDenialStillDeniesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "FoldOk/Target";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("FoldOk") { Name = "Fold Ok" },
                new MeshNode("Target", "FoldOk") { Name = "Target Node" });

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 15000)]
    public async Task AHealthyFoldThatSaysNo_StillAnswersAccessDenied()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "NoAccess", Name = "NoAccess" });

        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(NodePath)))
            .FirstAsync().ToTask();

        var ex = (await act.Should().ThrowAsync<DeliveryFailureException>()).Which;

        ex.Failure.ErrorType.Should().Be(ErrorType.Unauthorized,
            "the fold produced a verdict and the verdict was no — that is a real denial");
        ex.Message.Should().Contain("Access denied");
        ex.Message.Should().Contain("NoAccess");
    }
}
