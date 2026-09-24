using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A replica that has not passed its readiness validation must not re-key the shared NodeType
/// records by ADOPTING a prebuilt bundle — Systemorph/MeshWeaver#5544.</b>
///
/// <para><b>What was measured.</b> memex (the control instance), 2026-09-23/24: the 9260 pod
/// <c>memex-portal-deployment-7596dd76bb-cbbj8</c> never became Ready across five restarts, and on
/// every boot its bundle seeding logged <c>Prebuilt assembly ADOPTED for … (framework s9e58a…)</c>
/// 168 times before its sweep even started. Each adoption wrote <c>CompiledFrameworkVersion</c> and
/// the assembly coordinates into the SHARED record, so the two serving 9218 replicas' own
/// <c>/health</c> LIVE RECORD CENSUS read "21 NodeType record(s) were RE-KEYED to a framework this
/// replica does not run AFTER it booted" — every page of those types rendered area-not-found on the
/// replicas that were actually serving.</para>
///
/// <para><b>Why the existing gate did not stop it.</b> #3478 put the two COMPILE stamps behind
/// <see cref="MeshPublicationGate"/>; the adoption stamp in
/// <see cref="PrebuiltAssemblySeeder"/> carries the same fields into the same record and was never
/// offered to it. This is the inverse of #5353's bind-path yield: that one stops a LEAVING process
/// re-keying backwards, this one stops a process that has not JOINED yet re-keying forwards.</para>
///
/// <para>The three arms share one fixture and differ ONLY in the verdict the bake gate holds, so
/// each is the control for the others: provisional (held, released on admission), refused (never
/// written), unarmed (written at once — the pre-#3478 behaviour every unarmed host keeps).</para>
/// </summary>
public class AnUnadmittedProcessDoesNotReKeyByAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string LiveAssemblyPath = "live/AdoptionHeldType.dll";
    private const string LiveMvid = "22ee0000bbbb0000";

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The bake gate this process runs with — reassigned per arm, read live.</summary>
    private NodeTypeBakeGateState bake = new() { GatesReadiness = true };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new LiveBakeGate(() => bake)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private MeshPublicationGate Publication => Mesh.ServiceProvider.GetRequiredService<MeshPublicationGate>();

    private NodeTypeAdoptionRegistry Ledger => Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>();

    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(AnUnadmittedProcessDoesNotReKeyByAdoptionTest).Assembly.Location);

    /// <summary>A NodeType whose record names the build the SERVING replicas run.</summary>
    private async Task<string> CreateServedType(string id)
    {
        var typePath = $"{TestPartition}/{id}{suffix}";
        await MeshService.CreateNode(new MeshNode($"{id}{suffix}", TestPartition)
            {
                Name = id,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    CompilationStatus = CompilationStatus.Ok,
                    LatestAssemblyCollection = "assemblies",
                    LatestAssemblyPath = LiveAssemblyPath,
                    LatestAssemblyMvid = LiveMvid,
                    CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
                },
            })
            .Should().Within(20.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.LatestAssemblyMvid == LiveMvid,
                cancellationToken: TestContext.Current.CancellationToken);
        return typePath;
    }

    private IObservable<bool> Adopt(string typePath) =>
        PrebuiltAssemblySeeder.Seed(
            Mesh, typePath, BundleBytes(), pdbBytes: null,
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
            logger: null, dependencies: null, sourceFingerprint: null);

    private static bool NamesTheBundle(MeshNode? n, string bundleMvid, System.Text.Json.JsonSerializerOptions options)
        => string.Equals(n?.ContentAs<NodeTypeDefinition>(options)?.LatestAssemblyMvid, bundleMvid,
            StringComparison.Ordinal);

    /// <summary>
    /// 🚨 THE FIX. A provisional process adopts — for ITSELF: the ledger its sweep classifies from
    /// carries the adoption — while the shared record keeps the serving replicas' build until the
    /// bake passes; then the held stamp lands.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task AProvisionalProcess_AdoptsForItself_AndStampsOnlyWhenAdmitted()
    {
        var typePath = await CreateServedType("Held");
        var bundleMvid = ServedBuildIdentity.OfBytes(BundleBytes());
        bake.MarkRunning("enumerating dynamic NodeTypes");
        bake.Admission.Should().Be(MeshAdmission.Provisional, "precondition — the bake is still measuring");

        var adopted = await Adopt(typePath).Should().Within(20.Seconds())
            .Emit("the seed completes whether or not its stamp may be written",
                TestContext.Current.CancellationToken);
        adopted.Should().BeTrue(
            "the bytes ARE on the store for this framework — the sweep must be able to count the type "
            + "as baked, or a held adoption would turn into a recompile of every covered type");
        Ledger.AdoptedStamps.Keys.Should().Contain(typePath,
            "the sweep's classification overlay is this ledger; a held stamp must still reach it");
        Publication.HeldCount.Should().BeGreaterThan(0, "the stamp is HELD, not dropped");

        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => NamesTheBundle(n, bundleMvid!, Mesh.JsonSerializerOptions))
            .Should().NotEmit(3.Seconds(),
                "a process that has not passed readiness must not re-key the shared record — the "
                + "serving replicas would lose their usable build (#5544: 168 adoptions per boot)");

        bake.MarkComplete("baked — compiled=0 alreadyBaked=1");
        Publication.Reconsider();

        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => NamesTheBundle(n, bundleMvid!, Mesh.JsonSerializerOptions),
                "once admitted, the held adoption stamp lands — holding it forever would leave the "
                + "record naming a build nobody may serve after the roll",
                TestContext.Current.CancellationToken);
    }

    /// <summary>A REFUSED process neither stamps nor claims the adoption.</summary>
    [Fact(Timeout = 90_000)]
    public async Task ARefusedProcess_NeitherStampsNorAdopts()
    {
        var typePath = await CreateServedType("Refused");
        var bundleMvid = ServedBuildIdentity.OfBytes(BundleBytes());
        bake.MarkRunning("enumerating dynamic NodeTypes");
        bake.MarkOutcome(new PreWarmOutcome($"{TestPartition}/Other", PreWarmStatus.CompileError, "CS0117")
        {
            WasHealthyBeforeBake = true,
        });
        bake.Admission.Should().Be(MeshAdmission.Refused, "precondition — the bake measured a regression");

        var adopted = await Adopt(typePath).Should().Within(20.Seconds())
            .Emit("a refused seed still completes", TestContext.Current.CancellationToken);
        adopted.Should().BeFalse("a refused process adopts nothing into the mesh");

        await Mesh.GetMeshNodeStream(typePath)
            .Where(n => NamesTheBundle(n, bundleMvid!, Mesh.JsonSerializerOptions))
            .Should().NotEmit(3.Seconds(), "a refused process leaves no trace in the shared record");
    }

    /// <summary>🚨 CONTROL — unarmed: the identical call stamps at once, as every unarmed host always has.</summary>
    [Fact(Timeout = 90_000)]
    public async Task AnUnarmedProcess_StampsAtOnce()
    {
        bake = new NodeTypeBakeGateState { GatesReadiness = false };
        var typePath = await CreateServedType("Unarmed");
        var bundleMvid = ServedBuildIdentity.OfBytes(BundleBytes());
        bake.MarkRunning("enumerating dynamic NodeTypes");

        var adopted = await Adopt(typePath).Should().Within(20.Seconds())
            .Emit("the seed completes", TestContext.Current.CancellationToken);
        adopted.Should().BeTrue();
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => NamesTheBundle(n, bundleMvid!, Mesh.JsonSerializerOptions),
                "without an armed gate nothing is held — the arm that makes the provisional one "
                + "non-vacuous", TestContext.Current.CancellationToken);
    }

    private sealed class LiveBakeGate(Func<NodeTypeBakeGateState> current) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => current().Admission;

        public string AdmissionReason => current().AdmissionReason;
    }
}
