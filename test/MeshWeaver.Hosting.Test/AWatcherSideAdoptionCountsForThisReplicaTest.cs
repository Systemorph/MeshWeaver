using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #7015 review — the boot sweep's preflight offer pass can stop short of a type's bundle, so the
/// type is handed to the compile watcher without a local adoption, and the watcher's own per-type
/// pass then adopts it. On a readiness-gated (provisional) process that adoption lands in this
/// process's ledger while the shared stamp is HELD (#5544), so the watcher, reading only the shared
/// record, answers "nothing landed". The sweep must read its answer against this process's ledger:
/// a build this replica adopted and its store serves is baked here, never a readiness failure.
/// </summary>
public class AWatcherSideAdoptionCountsForThisReplicaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new StillMeasuring()));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(AWatcherSideAdoptionCountsForThisReplicaTest).Assembly.Location);

    /// <summary>A build-less record — the shared record the watcher reads while the stamp is held.</summary>
    private async Task<MeshNode> CreateBuildlessType(string id)
    {
        var typePath = $"{TestPartition}/{id}{suffix}";
        await MeshService.CreateNode(new MeshNode($"{id}{suffix}", TestPartition)
            {
                Name = id,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config",
                    CompilationStatus = CompilationStatus.Ok,
                },
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        return (await Mesh.GetMeshNodeStream(typePath)
            .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is not null)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private Task<PreWarmOutcome> Reclassify(MeshNode snapshot)
    {
        var watcherAnswer = new PreWarmOutcome(snapshot.Path, PreWarmStatus.Faulted,
            "the watcher found no usable build on the shared record and parked it");
        return DynamicTypePreWarmer.ReclassifyIfAdoptedHere(
                Mesh, watcherAnswer,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Path] = snapshot.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions),
                },
                new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase) { [snapshot.Path] = snapshot },
                TestTimeouts.Convergence, null)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the re-read answers once", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnAdoptionTheWatcherMadeOnAProvisionalProcess_IsBakedHere_NotAFailure()
    {
        var snapshot = await CreateBuildlessType("WatcherAdopted");

        // The watcher's per-type pass adopts on this provisional process: ledger yes, shared stamp held.
        (await PrebuiltAssemblySeeder.Seed(
                    Mesh, snapshot.Path, BundleBytes(), pdbBytes: null,
                    frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                    logger: null, dependencies: null, sourceFingerprint: null)
                .Should().Within(TestTimeouts.CrossSilo)
                .Emit(cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("precondition: the bytes reached this replica's store");
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>().AdoptedStamps.Keys
            .Should().Contain(snapshot.Path, "precondition: the adoption is in this process's ledger");

        var outcome = await Reclassify(snapshot);
        outcome.Status.Should().Be(PreWarmStatus.AlreadyBaked,
            $"this process adopted the build and its store serves it — the watcher's answer read the "
            + $"shared record, whose stamp is held; detail: {outcome.Detail}");
        outcome.ReachedUsableBuild.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutALocalAdoption_TheWatchersAnswerStands()
    {
        var snapshot = await CreateBuildlessType("NotAdopted");

        var outcome = await Reclassify(snapshot);
        outcome.Status.Should().Be(PreWarmStatus.Faulted,
            "negative control: nothing in this process's ledger, so the watcher's verdict is the answer");
    }

    private sealed class StillMeasuring : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => MeshAdmission.Provisional;

        public string AdmissionReason => "test: the bake is still measuring";
    }
}
