using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// 🚨 <b>Issue #2742, the node-operation half — a silent permission fold used to let a WRITE through.</b>
///
/// <para>The same defect as <see cref="PermissionFoldSilentTerminalTest"/>, in the other consumer.
/// <c>RlsNodeValidator</c>'s chain ends in <c>TakeDecisionOutsideGate().Timeout(budget).Catch(…)</c>,
/// which covers a value and a fault — but not the fold's THIRD terminal, completing without ever
/// emitting. <c>Take(1)</c> then completes empty, <c>Timeout</c> forwards that completion unchanged
/// (it bounds silence *before* a terminal, not an empty one), the <c>Catch</c> never fires, and the
/// validator yields NO <c>NodeValidationResult</c>. <c>RunCreationValidatorsObs</c>'s <c>Concat</c>
/// skips a validator that emits nothing, so "no verdict" read as "nothing objected".</para>
///
/// <para><b>Measured before the fix</b> — the probe this test grew out of: a user holding no grant
/// whatsoever created a node under a silent evaluator, and the result came back
/// <c>State = Active, CreatedBy = probeuser</c>. Not slow, not denied — written.</para>
///
/// <para>The answer is now <c>Unavailable</c> (retryable), never a denial, for the reason
/// <c>UnestablishedCheck</c> documents: no verdict was reached, so reporting one would send a
/// correctly-entitled caller to ask for permissions they may already hold. Fail-CLOSED either
/// way — the write does not happen.</para>
/// </summary>
public class RlsSilentFoldWriteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The partition whose fold is silent. Everything else evaluates normally so the mesh boots.</summary>
    private const string SilentScope = "SilentRls";

    private static EffectivePermissionsDelegate SilentOn(string scope)
        => (_, path, _) => path.StartsWith(scope, StringComparison.Ordinal)
            // What a CombineLatest emptied by a silent leg looks like from outside: completes,
            // no value, no error.
            ? Observable.Empty<Permission>()
            : Observable.Return(Permission.All);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .ConfigureHub(c => c.WithPermissionEvaluator(SilentOn(SilentScope)))
            .ConfigureDefaultNodeHub(c => c.WithPermissionEvaluator(SilentOn(SilentScope)));

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 60_000)]
    public async Task ASilentFold_RefusesTheWrite_InsteadOfPerformingIt()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext { ObjectId = "probeuser", Name = "probeuser" });
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = MeshNode.FromPath($"{SilentScope}/Child") with
        {
            Name = "Child",
            NodeType = "Markdown",
            Content = "probe"
        };

        // 🚨 THE REGRESSION PIN. Before the fix this returned a live MeshNode — the write happened.
        // ObserveCompletion, never `.ToTask()` (ObservableToTaskBridgeGuard) — it settles off the
        // signalling thread and faults the task with the ORIGINAL exception.
        Func<Task> act = () => meshService.CreateNode(node)
            .FirstAsync()
            .ObserveCompletion(ex => Output.WriteLine($"[late fault] {ex}"));
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;

        ex.Message.Should().Contain("could not be established",
            "no verdict was reached, so the honest answer is an availability failure");
        ex.Message.Should().Contain("without producing a verdict",
            "the operator has to be able to tell a SILENT fold from a stalled or faulted one — "
            + "the three do not have the same cause");
        ex.Message.Should().NotContain("Access denied",
            "the fold established nothing about this caller's rights, so claiming a denial would "
            + "send them to request permissions they may already hold");
    }

    [Fact(Timeout = 60_000)]
    public async Task AHealthyFold_OnTheSameMesh_StillWrites()
    {
        // The over-reach control: the fix must refuse the SILENT case only. A validator that started
        // refusing every write would satisfy the pin above while breaking the portal.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetHostIdentity(new AccessContext { ObjectId = "probeuser", Name = "probeuser" });
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var node = MeshNode.FromPath("HealthyRls/Child") with
        {
            Name = "Healthy Child",
            NodeType = "Markdown",
            Content = "probe"
        };

        var created = await meshService.CreateNode(node).Should().Emit();
        created.State.Should().Be(MeshNodeState.Active);
    }
}
