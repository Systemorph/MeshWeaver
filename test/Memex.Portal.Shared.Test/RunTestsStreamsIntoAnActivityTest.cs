#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.InMesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <c>RunTests</c> (the <c>run_tests</c> tool, <c>memex tests</c>) keeps ONE subscription to a
/// node's streaming <c>Tests</c> area and turns its frames into an activity log: a line per case as
/// it runs and as it lands, the case's output, and a terminal status that is the verdict.
///
/// <para>Why it exists: rendering the area RUNS the suite, and a one-shot <c>get …/area/Tests</c>
/// opens a fresh subscription each time — polling it re-runs every live case (measured on
/// memex.meshweaver.cloud: each read filed the Maintenance suite's request nodes again).</para>
/// </summary>
public class RunTestsStreamsIntoAnActivityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Node = "RunTestsProbe";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(TestUsers.SampleUsers())
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" })
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout => layout
                .WithView("Tests", (LayoutAreaHost host, RenderingContext _) =>
                    MeshTestRunner.Area(host, "Probe",
                    [
                        MeshTestCase.Live("slow", log =>
                        {
                            log("contacted the service");
                            return Observable.Timer(TimeSpan.FromMilliseconds(2500)).Select(_ => Unit.Default);
                        }),
                        MeshTestCase.Of("fails", () => throw new InvalidOperationException("the assertion message")),
                    ]))));

    [HubFact]
    public async Task RunTests_LogsEveryCase_AndEndsWithTheVerdict()
    {
        var answer = await new MeshOperations(Mesh).RunTests("@" + Node, timeoutSeconds: 60)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        using var doc = JsonDocument.Parse(answer);
        Assert.Equal("Dispatched", doc.RootElement.GetProperty("status").GetString());
        var activityPath = doc.RootElement.GetProperty("activityPath").GetString()!;

        var log = await Mesh.GetMeshNodeStream(activityPath)
            .Select(node => node.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l?.Status is ActivityStatus.Succeeded or ActivityStatus.Failed)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60)).Await();

        var lines = log!.Messages.Select(m => m.Message).ToList();
        Assert.Equal(ActivityStatus.Failed, log.Status);
        Assert.Contains(lines, l => l.StartsWith("▶ slow", StringComparison.Ordinal) && l.Contains("contacted the service"));
        Assert.Contains(lines, l => l.StartsWith("✅", StringComparison.Ordinal) && l.Contains("slow"));
        Assert.Contains(lines, l => l.StartsWith("❌", StringComparison.Ordinal) && l.Contains("the assertion message"));
        Assert.Contains(lines, l => l.Contains("1/2 passed", StringComparison.Ordinal));
    }
}
