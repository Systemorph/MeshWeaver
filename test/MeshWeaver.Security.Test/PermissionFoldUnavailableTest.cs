using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Security;
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

/// <summary>
/// The two paths the short-circuit pin evaluates. Declared on the attribute so the message
/// definitions, the evaluator and the assertions all name the SAME strings.
/// </summary>
public static class ShortCircuitPaths
{
    /// <summary>The check that reaches a decision: its fold faults ⇒ Undetermined ⇒ Unavailable.</summary>
    public const string Decisive = "ShortCircuit/Decisive";

    /// <summary>The check that must NEVER be evaluated once <see cref="Decisive"/> has decided.</summary>
    public const string Later = "ShortCircuit/Later";
}

/// <summary>Decisive check FIRST, so the later one must never run.</summary>
public class DecisiveFirstPermissionAttribute() : RequiresPermissionAttribute(Permission.Read)
{
    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        yield return (ShortCircuitPaths.Decisive, Permission.Read);
        yield return (ShortCircuitPaths.Later, Permission.Read);
    }
}

/// <summary>
/// The same two checks in the OPPOSITE order — a GRANTED check first. Its only job is to prove the
/// termination is conditional: a granted outcome decides nothing, so the pipeline must go on.
/// </summary>
public class GrantedFirstPermissionAttribute() : RequiresPermissionAttribute(Permission.Read)
{
    /// <inheritdoc />
    public override IEnumerable<(string Path, Permission Permission)> GetPermissionChecks(
        IMessageDelivery delivery, string hubPath)
    {
        yield return (ShortCircuitPaths.Later, Permission.Read);
        yield return (ShortCircuitPaths.Decisive, Permission.Read);
    }
}

/// <summary>Probe carrying two checks, the decisive one first.</summary>
[DecisiveFirstPermission]
public record DecisiveFirstProbe : IRequest<ShortCircuitProbeResponse>;

/// <summary>Probe carrying the same two checks, the granted one first.</summary>
[GrantedFirstPermission]
public record GrantedFirstProbe : IRequest<ShortCircuitProbeResponse>;

/// <summary>Never actually sent — the pipeline refuses both probes before any handler runs.</summary>
public record ShortCircuitProbeResponse;

/// <summary>
/// Pins that <c>AccessControlPipeline</c> STOPS evaluating permission checks once one of them has
/// decided the delivery (PR #989 review finding on issue #974).
///
/// <para><b>What regressed-shaped code looks like.</b> The chain used to carry a <c>decided</c> bool
/// that all three Subscribe callbacks consulted. That suppressed the duplicate
/// <see cref="DeliveryFailure"/> post — so every outcome assertion still passed — while the Rx chain
/// ran on: <c>Concat</c> kept subscribing the remaining <c>CheckPermissionOutcome</c> folds and every
/// one of them was evaluated in full. Nothing observable in the ANSWER changes, which is exactly why
/// this needs its own pin: the defect is invisible to every other test in this file.</para>
///
/// <para><b>Why it matters here specifically.</b> The outcome that triggers it most is
/// <c>Undetermined</c>, and Undetermined means a degraded dependency — a wedged access cache, an
/// unreachable Postgres, a hub mid-disposal. Every superfluous check re-hits that same sick
/// dependency, so the amplification peaks exactly when the system can least afford it. This repo has
/// twice watched that shape become the outage (the 429 wedge that leaked round hubs into a 502; the
/// NotFound storm that wedged a portal).</para>
///
/// <para><b>Why the assertion is trustworthy.</b> "A thing did not happen" is only meaningful if the
/// instrument that would have recorded it works, and if the observation happens after the thing
/// would have occurred. Both are handled by asking a SECOND probe — one whose first check is granted
/// — to evaluate the same path afterwards: its recorded evaluation is the positive barrier (no
/// sleep), and it is also the proof that termination is conditional rather than "stop after check
/// one". A pipeline that ran every check appends a third entry; a pipeline that stopped
/// unconditionally never appends the second.</para>
/// </summary>
public class PermissionCheckShortCircuitTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "ShortCircuit/Target";

    /// <summary>
    /// Every path the evaluator was actually asked about, in order. INSTANCE state — its lifetime is
    /// this test's mesh (AGENTS.md → no static collections), so nothing bleeds into the next test.
    /// </summary>
    private readonly ConcurrentQueue<string> evaluated = new();

    /// <summary>What a wedged access cache / unreachable store looks like from inside the fold.</summary>
    private sealed class FoldFaultException() : Exception("access assignment store unreachable");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode("ShortCircuit") { Name = "Short Circuit" },
                new MeshNode("Target", "ShortCircuit") { Name = "Target Node" })
            .ConfigureDefaultNodeHub(c => c
                .WithType(typeof(DecisiveFirstProbe), nameof(DecisiveFirstProbe))
                .WithType(typeof(GrantedFirstProbe), nameof(GrantedFirstProbe))
                .WithType(typeof(ShortCircuitProbeResponse), nameof(ShortCircuitProbeResponse))
                // The evaluator is only invoked when a check is SUBSCRIBED — CheckPermissionOutcome
                // builds it inside Observable.Defer — so this queue records exactly the checks the
                // pipeline actually ran, which is the property under test.
                .WithPermissionEvaluator((_, path, _) =>
                {
                    if (path is not (ShortCircuitPaths.Decisive or ShortCircuitPaths.Later))
                        return Observable.Return(Permission.All);   // ordinary hub traffic

                    evaluated.Enqueue(path);
                    return path == ShortCircuitPaths.Decisive
                        ? Observable.Throw<Permission>(new FoldFaultException())
                        : Observable.Return(Permission.All);
                }));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithType(typeof(DecisiveFirstProbe), nameof(DecisiveFirstProbe))
            .WithType(typeof(GrantedFirstProbe), nameof(GrantedFirstProbe))
            .WithType(typeof(ShortCircuitProbeResponse), nameof(ShortCircuitProbeResponse));

    [Fact(Timeout = 15000)]
    public async Task ADecidedCheck_StopsTheRemainingChecks_FromBeingEvaluated()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Entitled", Name = "Entitled" });
        var target = new Address(NodePath);

        // 1. Decisive check FIRST. It faults ⇒ Undetermined ⇒ the delivery is refused as Unavailable.
        Func<Task> decisiveFirst = () => client
            .Observe<ShortCircuitProbeResponse>(new DecisiveFirstProbe(), o => o.WithTarget(target))
            .FirstAsync().ToTask();

        var ex = (await decisiveFirst.Should().ThrowAsync<DeliveryFailureException>()).Which;

        ex.Failure.ErrorType.Should().Be(ErrorType.Unavailable,
            "the outcome semantics are untouched — a fold that reached no verdict still answers "
            + "Unavailable, never a denial");

        // 2. THE BARRIER. Same two checks, granted one first — so this probe is guaranteed to
        //    evaluate the later path, and its arrival orders the assertion after anything probe 1
        //    could still have been doing. No sleep, no "wait a bit and hope".
        Func<Task> grantedFirst = () => client
            .Observe<ShortCircuitProbeResponse>(new GrantedFirstProbe(), o => o.WithTarget(target))
            .FirstAsync().ToTask();

        await grantedFirst.Should().ThrowAsync<DeliveryFailureException>();

        // 3. The pin. Three entries, in this exact order:
        //      Decisive  — probe 1's first check, which decided and TERMINATED the chain
        //      Later     — probe 2's first check: granted, so the chain rightly continued
        //      Decisive  — probe 2's second check, which then decided
        //    A pipeline that kept going after a decision inserts a fourth entry (Later) at index 1.
        //    A pipeline that terminated unconditionally never reaches probe 2's second check.
        string.Join(" → ", evaluated).Should().Be(
            $"{ShortCircuitPaths.Decisive} → {ShortCircuitPaths.Later} → {ShortCircuitPaths.Decisive}",
            "a check whose result cannot change the answer must never be evaluated — and a GRANTED "
            + "check decides nothing, so the ones after it must be");

        evaluated.Count(p => p == ShortCircuitPaths.Later).Should().Be(1,
            "the later check runs exactly once — for the probe that had not decided before it");
    }
}
