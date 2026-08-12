using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

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
/// <c>AddRowLevelSecurity</c>, so pipeline-installed ⟺ evaluator-installed. This fixture therefore
/// wires the pipeline BY HAND onto a mesh with no evaluator — the embedder's mistake, and what the
/// tree becomes the day that one call site is split.</para>
/// </summary>
public class MissingEvaluatorFailsClosedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "Ungated/Target";

    /// <summary>
    /// Deliberately NOT <c>ConfigureMeshBase</c>: that calls <c>AddRowLevelSecurity()</c>, which
    /// installs the evaluator and the pipeline together. Here the pipeline goes on ALONE.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
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
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// THE REGRESSION PIN: the request must be refused. Before the fix it was SERVED — every
    /// permission-bearing message on the mesh sailed straight through the gate.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task AMissingEvaluator_RefusesTheDelivery_NeverServesIt()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Anyone", Name = "Anyone" });

        // GetDataRequest carries [RequiresPermission(Permission.Read)].
        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(NodePath)))
            .FirstAsync().ToTask();

        var ex = (await act.Should().ThrowAsync<DeliveryFailureException>(
            "a gate with no evaluator reached no verdict, so it must refuse — silently allowing was "
            + "an authorization bypass that looked identical to a mesh where every check passed")).Which;

        ex.Failure.ErrorType.Should().Be(ErrorType.Unavailable,
            "no verdict was reached — asserting the user lacks a permission would be a fact nobody "
            + "established (issue #974's distinction, applied to the missing-evaluator case)");
    }

    /// <summary>
    /// The refusal has to be actionable for the operator: it names the missing registration AND the
    /// explicit opt-out, so whoever reads it can tell which of the two situations they are in.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task TheRefusal_NamesExactlyWhatToRegister()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Anyone", Name = "Anyone" });

        Func<Task> act = () => client
            .Observe(new GetDataRequest(new UnifiedReference("data:")),
                o => o.WithTarget(new Address(NodePath)))
            .FirstAsync().ToTask();

        var ex = (await act.Should().ThrowAsync<DeliveryFailureException>()).Which;

        ex.Message.Should().Contain("AddRowLevelSecurity");
        ex.Message.Should().Contain("AllowUnsecuredMesh");
        ex.Message.Should().NotContain("Access denied",
            "nobody decided this caller lacks anything");
    }
}

/// <summary>
/// The other side of the same rule: a mesh that DECLARES itself ungated is served, because the
/// declaration is the statement the previous design was missing. Without this, "fail closed on a
/// missing evaluator" would have no escape hatch and would break every legitimately-unsecured host
/// — which is how a safety change turns into a change people disable.
///
/// <para>The distinction being pinned is the whole point: identical evaluator state (none), opposite
/// outcomes, decided by whether the host SAID SO. Never inferred from the absence of a value.</para>
/// </summary>
public class DeclaredUnsecuredMeshStillServesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodePath = "Declared/Target";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
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
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 15000)]
    public async Task ADeclaredUngatedMesh_IsNotRefusedByTheGate()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext { ObjectId = "Anyone", Name = "Anyone" });

        DeliveryFailureException? refusal = null;
        try
        {
            await client
                .Observe(new GetDataRequest(new UnifiedReference("data:")),
                    o => o.WithTarget(new Address(NodePath)))
                .FirstAsync().ToTask();
        }
        catch (DeliveryFailureException ex)
        {
            refusal = ex;
        }

        // Asserting on the REFUSAL rather than on overall success on purpose: this fixture is a
        // deliberately minimal mesh, so the read may not complete for reasons that have nothing to
        // do with access control. What must not happen is the ACCESS GATE refusing it — that is the
        // exact distinction under test, and it is unaffected by anything else the request does.
        refusal?.Message.Should().NotContain("AllowUnsecuredMesh",
            "the host already declared this mesh ungated, so the missing-evaluator gate must stand "
            + "down — a fail-closed default with no explicit way out is one that hosts disable");
    }
}
