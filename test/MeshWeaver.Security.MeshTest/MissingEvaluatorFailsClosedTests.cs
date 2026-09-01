using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Security.MeshTest;

/// <summary>
/// 🚨 THE GATE MUST NOT SKIP ITSELF. <c>AccessControlPipeline</c> used to compute
/// <c>rlsDisabled = Configuration.Get&lt;EffectivePermissionsDelegate&gt;() is null</c> and, when
/// true, invoke the downstream handler for EVERY <c>[RequiresPermission]</c> message — i.e. a mesh
/// with the gate installed but no evaluator behaved EXACTLY like a mesh where every check passed.
///
/// <para>That is the runtime form of the rule AGENTS.md states for CI: a gate never tests its own
/// inputs, because "the gate never ran" and "the gate passed" then become indistinguishable, and a
/// check that passes on no evidence is worse than one that flakes. The condition is an
/// <b>input-shaped</b> one — "is my evaluator configured?" — and it silently resolved to ALLOW.</para>
///
/// <para><b>Why the answer is Unavailable and not Unauthorized.</b> Same reason as issue #974: no
/// verdict was reached. Claiming the user lacks a permission would be a false, actionable-looking
/// statement. The delivery is refused either way — that is the part that must never regress.</para>
///
/// <para><b>Reaching the state.</b> In the shipped tree it is unreachable by construction:
/// <c>AddAccessControlPipeline</c> has exactly one caller and it sits in the same expression as
/// <c>AddRowLevelSecurity</c>, so pipeline-installed ⟺ evaluator-installed. This suite therefore
/// wires the pipeline BY HAND onto a mesh with no evaluator — the embedder's mistake, and what the
/// tree becomes the day that one call site is split.</para>
///
/// <para>🚨 <b>Why this suite is the proof of the pre-boot substitution seam.</b> Its premise is a
/// service that must be ABSENT. There is no way to express that against the already-composed mesh
/// an in-mesh <c>Tests</c> area boots into, and no additive registration can un-register anything:
/// the ONLY expressible form is "boot a mesh composed like THIS". <see cref="ConfigureMesh"/> is
/// that declaration, and the lane honours it by standing up a private mesh for this class alone.
/// Deliberately NOT the shared default composition, which calls <c>AddRowLevelSecurity()</c> and
/// installs the evaluator and the pipeline together.</para>
/// </summary>
public static class MissingEvaluatorFailsClosedTests
{
    private const string NodePath = "Ungated/Target";

    /// <summary>
    /// The declaration the install-and-execute lane reads: the gate, with NO evaluator behind it.
    /// </summary>
    /// <param name="builder">A fresh builder the lane constructed for this suite.</param>
    /// <returns>The configured builder.</returns>
    public static MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddGraph()
            .AddMeshNodes(
                new MeshNode("Ungated") { Name = "Ungated" },
                new MeshNode("Target", "Ungated") { Name = "Target Node" })
            // The gate, with no evaluator behind it.
            .ConfigureDefaultNodeHub(c => c.AddAccessControlPipeline())
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TimeSpan.FromMilliseconds(500))
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// THE REGRESSION PIN: the request must be refused. Before the fix it was SERVED — every
    /// permission-bearing message on the mesh sailed straight through the gate.
    /// </summary>
    /// <param name="services">The declared mesh's root service provider.</param>
    /// <param name="client">A client hub on that mesh.</param>
    /// <returns>A stream that completes when the assertion holds and faults when it does not.</returns>
    public static IObservable<Unit> AMissingEvaluator_RefusesTheDelivery_NeverServesIt(
        IServiceProvider services, IMessageHub client)
        => Refusal(services, client)
            .Select(ex =>
            {
                if (ex.Failure.ErrorType != ErrorType.Unavailable)
                    throw new InvalidOperationException(
                        $"expected ErrorType.Unavailable, got {ex.Failure.ErrorType}: no verdict was "
                        + "reached, so asserting the user lacks a permission would be a fact nobody "
                        + "established (issue #974's distinction, applied to the missing-evaluator "
                        + "case)");
                return Unit.Default;
            });

    /// <summary>
    /// The refusal has to be actionable for the operator: it names the missing registration AND the
    /// explicit opt-out, so whoever reads it can tell which of the two situations they are in.
    /// </summary>
    /// <param name="services">The declared mesh's root service provider.</param>
    /// <param name="client">A client hub on that mesh.</param>
    /// <returns>A stream that completes when the assertion holds and faults when it does not.</returns>
    public static IObservable<Unit> TheRefusal_NamesExactlyWhatToRegister(
        IServiceProvider services, IMessageHub client)
        => Refusal(services, client)
            .Select(ex =>
            {
                Contains(ex.Message, "AddRowLevelSecurity");
                Contains(ex.Message, "AllowUnsecuredMesh");
                if (ex.Message.Contains("Access denied", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "the refusal says \"Access denied\" — nobody decided this caller lacks "
                        + "anything: " + ex.Message);
                return Unit.Default;
            });

    /// <summary>
    /// The read, as a stream that yields the gate's refusal. A SERVED read is the regression, so
    /// the success arm throws: "no exception" is the failure this suite exists to catch.
    /// </summary>
    private static IObservable<DeliveryFailureException> Refusal(
        IServiceProvider services, IMessageHub client)
        => Observable.Defer(() =>
        {
            client.ServiceProvider.GetRequiredService<AccessService>()
                .SetCircuitContext(new AccessContext { ObjectId = "Anyone", Name = "Anyone" });
            // GetDataRequest carries [RequiresPermission(Permission.Read)].
            return client
                .Observe(new GetDataRequest(new UnifiedReference("data:")),
                    o => o.WithTarget(new Address(NodePath)))
                .FirstAsync()
                .SelectMany(_ => Observable.Throw<DeliveryFailureException>(
                    new InvalidOperationException(
                        "the read was SERVED. A gate with no evaluator reached no verdict, so it "
                        + "must refuse — silently allowing was an authorization bypass that looked "
                        + "identical to a mesh where every check passed.")))
                .Catch((DeliveryFailureException ex) => Observable.Return(ex));
        });

    private static void Contains(string message, string expected)
    {
        if (!message.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the refusal does not name '{expected}', so an operator cannot tell which of the "
                + $"two situations they are in: {message}");
    }
}

/// <summary>
/// The other side of the same rule: a mesh that DECLARES itself ungated is served, because the
/// declaration is the statement the previous design was missing. Without this, "fail closed on a
/// missing evaluator" would have no escape hatch and would break every legitimately-unsecured host
/// — which is how a safety change turns into a change people disable.
///
/// <para>The distinction being pinned is the whole point: identical evaluator state (none), opposite
/// outcomes, decided by whether the host SAID SO. Never inferred from the absence of a value. Two
/// meshes with the SAME absent service and opposite verdicts is also why the seam has to be
/// per-suite rather than per-run: one booted mesh cannot be both.</para>
/// </summary>
public static class DeclaredUnsecuredMeshStillServesTests
{
    private const string NodePath = "Declared/Target";

    /// <summary>The declaration: the same missing evaluator, plus the explicit opt-out.</summary>
    /// <param name="builder">A fresh builder the lane constructed for this suite.</param>
    /// <returns>The configured builder.</returns>
    public static MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddInMemoryPersistence()
            .AddGraph()
            .AddMeshNodes(
                new MeshNode("Declared") { Name = "Declared" },
                new MeshNode("Target", "Declared") { Name = "Target Node" })
            .ConfigureDefaultNodeHub(c => c
                .AddAccessControlPipeline()
                .AllowUnsecuredMesh("integration fixture: exercises routing, not access"))
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TimeSpan.FromMilliseconds(500))
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// Asserts on the REFUSAL rather than on overall success on purpose: this fixture is a
    /// deliberately minimal mesh, so the read may not complete for reasons that have nothing to do
    /// with access control. What must not happen is the ACCESS GATE refusing it — that is the exact
    /// distinction under test, and it is unaffected by anything else the request does.
    /// </summary>
    /// <param name="services">The declared mesh's root service provider.</param>
    /// <param name="client">A client hub on that mesh.</param>
    /// <returns>A stream that completes when the assertion holds and faults when it does not.</returns>
    public static IObservable<Unit> ADeclaredUngatedMesh_IsNotRefusedByTheGate(
        IServiceProvider services, IMessageHub client)
        => Observable.Defer(() =>
        {
            client.ServiceProvider.GetRequiredService<AccessService>()
                .SetCircuitContext(new AccessContext { ObjectId = "Anyone", Name = "Anyone" });
            return client
                .Observe(new GetDataRequest(new UnifiedReference("data:")),
                    o => o.WithTarget(new Address(NodePath)))
                .FirstAsync()
                .Select(_ => Unit.Default)
                .Catch((DeliveryFailureException ex) =>
                    ex.Message.Contains("AllowUnsecuredMesh", StringComparison.Ordinal)
                        ? Observable.Throw<Unit>(new InvalidOperationException(
                            "the missing-evaluator gate refused a mesh the host already declared "
                            + "ungated — a fail-closed default with no explicit way out is one that "
                            + "hosts disable: " + ex.Message))
                        : Observable.Return(Unit.Default));
        });
}
