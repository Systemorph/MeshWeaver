using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A readiness-gated sweep must hear its OWN compile — Systemorph/MeshWeaver#5544.</b>
///
/// <para><b>What was measured.</b> memex's 9260 pod, 2026-09-24 03:55Z onwards: its batched source
/// discovery failed, the sweep fell back to the activation path for 107 pending NodeTypes, and the
/// pod's log then read <c>DynamicTypePreWarmer: … → TimedOut — 300.0 s</c> for EVERY type, one
/// after another. 107 × 5 minutes is longer than the startup probe's three hours, so the pod was
/// killed and restarted into the same sweep five times; its readiness message said "enumerating
/// dynamic NodeTypes" throughout.</para>
///
/// <para><b>Why every type timed out.</b> The activation path's per-type wait (<c>WarmOne</c>) read
/// ONLY the shared record. On a pod whose bake gates readiness, the compile's stamp on that record
/// is HELD by <see cref="MeshPublicationGate"/> until the bake passes (#3478) — and the bake cannot
/// pass until this wait answers. A deadlock by construction, which #3478 already fixed for the
/// recovery watch (<see cref="LocalNodeTypeBuilds"/>) and left in place here.</para>
///
/// <para>The two arms differ only in whether the gate is armed. Unarmed, the stamp is written and
/// the record answers — the control that proves the fixture really compiles. Armed and provisional,
/// the record CANNOT answer (asserted), so a Compiled outcome can only have come from the local
/// witness.</para>
/// </summary>
public class AGatedSweepHearsItsOwnCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    private NodeTypeBakeGateState bake = new() { GatesReadiness = true };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new LiveBakeGate(() => bake)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task<string> CreateNeverBuiltType()
    {
        var id = $"Warm{suffix}";
        var typePath = $"{TestPartition}/{id}";
        foreach (var node in new[]
                 {
                     new MeshNode(id, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = id,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition { Configuration = "config => config" },
                     },
                     new MeshNode("Main", $"{typePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration
                         {
                             Language = "csharp",
                             Code = $"public static class Warm{suffix}Api {{ public static int Answer() => 1; }}",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{node.Path} must exist before the sweep warms it", TestContext.Current.CancellationToken);
        return typePath;
    }

    private IObservable<PreWarmOutcome> Warm(string typePath) =>
        DynamicTypePreWarmer.WarmOne(
            Mesh.GetWorkspace(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            typePath, Budget,
            Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("warm"));

    /// <summary>
    /// 🚨 THE FIX. Provisional: the stamp is held, the record cannot answer, and the warm still
    /// reports Compiled well inside its budget — from the process-local witness.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AProvisionalSweep_ReportsCompiled_ThoughItsStampIsHeld()
    {
        var typePath = await CreateNeverBuiltType();
        bake.MarkRunning("enumerating dynamic NodeTypes");
        bake.Admission.Should().Be(MeshAdmission.Provisional, "precondition — the bake gates and is measuring");

        var outcome = await Warm(typePath).Should().Within(Budget + TestTimeouts.Convergence)
            .Emit("WarmOne always reaches exactly one outcome", TestContext.Current.CancellationToken);
        Output.WriteLine("outcome: {0} — {1} — {2}", outcome.Status, outcome.Detail ?? "(no detail)", outcome.Duration);

        outcome.Status.Should().Be(PreWarmStatus.Compiled,
            "the compile settled on THIS process; waiting for a stamp this process's own admission "
            + "holds is the deadlock that timed out all 107 types on memex's 9260 pod");
        outcome.Duration.Should().BeLessThan(Budget,
            "a Compiled that arrives only at the deadline would be the timeout wearing another label");

        var record = await Mesh.GetMeshNodeStream(typePath).Take(1)
            .Should().Within(10.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        var def = record?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions);
        CarriesThisProcessBuild(def).Should().BeFalse(
                "the shared record must NOT show the build — its stamp is held — or this case would "
                + "not prove the answer came from the local witness");
        Mesh.ServiceProvider.GetRequiredService<MeshPublicationGate>().HeldCount.Should().BeGreaterThan(0,
            "the compile stamp waits for the bake's verdict");
    }

    /// <summary>🚨 CONTROL — unarmed: the stamp is written and the record answers, as it always did.</summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUnarmedSweep_ReportsCompiled_FromTheRecord()
    {
        bake = new NodeTypeBakeGateState { GatesReadiness = false };
        var typePath = await CreateNeverBuiltType();
        bake.MarkRunning("enumerating dynamic NodeTypes");

        var outcome = await Warm(typePath).Should().Within(Budget + TestTimeouts.Convergence)
            .Emit("WarmOne always reaches exactly one outcome", TestContext.Current.CancellationToken);
        Output.WriteLine("outcome: {0} — {1} — {2}", outcome.Status, outcome.Detail ?? "(no detail)", outcome.Duration);

        outcome.Status.Should().Be(PreWarmStatus.Compiled, "the fixture type compiles");
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => CarriesThisProcessBuild(n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)),
                "unarmed, nothing is held: the record carries the build",
                TestContext.Current.CancellationToken);
    }

    /// <summary>The record names a successful build for THIS process's framework — what a written
    /// compile stamp leaves, and what a held one cannot.</summary>
    private static bool CarriesThisProcessBuild(NodeTypeDefinition? d)
        => d is { CompilationStatus: CompilationStatus.Ok }
           && !string.IsNullOrEmpty(d.LatestAssemblyPath)
           && string.Equals(d.CompiledFrameworkVersion, PrebuiltAssemblySeeder.LiveFrameworkMvid, StringComparison.Ordinal);

    private sealed class LiveBakeGate(Func<NodeTypeBakeGateState> current) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => current().Admission;

        public string AdmissionReason => current().AdmissionReason;
    }
}
