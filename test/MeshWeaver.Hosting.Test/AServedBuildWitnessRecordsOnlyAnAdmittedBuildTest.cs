using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The durable "this platform build has served here" witness</b> (#5544, review of #5725).
/// This is the marker that lets a restarted pod of the serving image know it IS the image the
/// rollout falls back on, so its bake gate never refuses it.
///
/// <para>Per-type provenance could not carry that fact. An ordinary roll compiles nothing (the
/// compatibility key is equal across builds of one epoch), and prebuilt adoption keeps the
/// PRODUCER's platform version. So a serving image can leave no record naming itself. The marker
/// is independent of both. It must also be impossible for a REFUSED pod to write it, or a bad image
/// would exempt itself on its second boot. These cases assert that on the real mesh storage, with
/// the real <see cref="NodeTypeBakeGateState"/> driving the real <see cref="MeshPublicationGate"/>.</para>
/// </summary>
public class AServedBuildWitnessRecordsOnlyAnAdmittedBuildTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly NodeTypeBakeGateState bake = new() { GatesReadiness = true };

    private const string Build = "3.0.0-ci.9218";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new LiveBakeGate(() => bake)));

    private MeshPublicationGate Publication => Mesh.ServiceProvider.GetRequiredService<MeshPublicationGate>();

    private IObservable<bool> Served() => ServedBuildWitness.HasServed(Mesh, Build, null);

    [Fact(Timeout = 60_000)]
    public async Task AnAdmittedPod_WritesTheWitness_AndARestartReadsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Served().Should().Within(TestTimeouts.Convergence).Emit("the read answers", ct))
            .Should().BeFalse("nothing has served yet");

        bake.MarkRunning("enumerating dynamic NodeTypes");
        await ServedBuildWitness.Record(Mesh, Build, null).LastOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("a held offer completes at once", ct);
        (await Served().Should().Within(TestTimeouts.Convergence).Emit("the read answers", ct))
            .Should().BeFalse("a pod still measuring has not served — the offer is HELD, not written");

        bake.MarkComplete("baked");
        Publication.Reconsider();

        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Served())
            .Where(served => served)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an admitted pod releases the held witness", ct);

        // What the restarted pod does with it: the report carries ServedBefore, and a failure of a
        // type whose only working build an OLDER image produced no longer gates.
        var report = await ServedBuildWitness.Annotate(Mesh,
                new NodeTypeBakeReport(
                    [new NodeTypeBakeEntry("Crm/Contact", BakeState.BytesMissing) { ProducedByPlatformBuild = "3.0.0-ci.9100" }],
                    "framework-1") { LivePlatformVersion = Build },
                null)
            .Should().Within(TestTimeouts.Convergence).Emit("the annotation answers", ct);
        report.ServedBefore.Should().BeTrue();
        report.ThisBuildHasServed.Should().BeTrue();
        report.GateRelevant.Should().BeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task ARefusedPod_NeverWritesTheWitness()
    {
        var ct = TestContext.Current.CancellationToken;
        bake.MarkRunning("enumerating dynamic NodeTypes");
        await ServedBuildWitness.Record(Mesh, Build, null).LastOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("a held offer completes at once", ct);

        bake.MarkOutcome(new PreWarmOutcome("Crm/Contact", PreWarmStatus.CompileError, "CS0246"));
        bake.MarkComplete("baked");
        Publication.Reconsider();

        bake.Admission.Should().Be(MeshAdmission.Refused);
        Publication.DiscardedCount.Should().BeGreaterThan(0, "the held witness is discarded, not written");
        (await Served().Should().Within(TestTimeouts.Convergence).Emit("the read answers", ct))
            .Should().BeFalse("a refused image must never exempt itself on its next boot");
    }

    private sealed class LiveBakeGate(Func<NodeTypeBakeGateState> current) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => current().Admission;

        public string AdmissionReason => current().AdmissionReason;
    }
}
