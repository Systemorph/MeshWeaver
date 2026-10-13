#pragma warning disable CS1591

using System;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// MeshWeaver.Plugins#3513: <c>run_tests</c>'s <c>timeoutSeconds</c> bounds the WHOLE run, the
/// preflight included. The preflight (read the versions under test, converge the mesh to its target
/// set) used to run OUTSIDE the budget, so a preflight that took longer than the budget held the
/// activity at its title line with no <c>Failed</c> — measured on a portal: dispatched with 300 s,
/// silent for 8 min 35 s. Here the preflight NEVER answers; the run must still end <c>Failed</c>
/// at its budget, naming the phase it was stuck in.
/// </summary>
public class RunTestsBudgetCoversThePreflightTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Node = "RunTestsPreflightProbe";

    /// <summary>A preflight that never decides — the shape of a convergence pass outliving the run's budget.</summary>
    private sealed class NeverAnsweringPreflight : ITestRunPreflight
    {
        public IObservable<TestRunPreflightResult> Prepare(bool mayConverge) => Observable.Never<TestRunPreflightResult>();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(TestUsers.SampleUsers())
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" })
            .ConfigureServices(services => services.AddSingleton<ITestRunPreflight>(new NeverAnsweringPreflight()))
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout => layout
                .WithView("Tests", (LayoutAreaHost host, RenderingContext _) =>
                    MeshTestRunner.Area(host, "Probe", [MeshTestCase.Of("passes", () => { })]))));

    [HubFact]
    public async Task RunTests_PreflightOutlivingTheBudget_EndsFailed_NamingThePreflight()
    {
        var answer = await new MeshOperations(Mesh).RunTests("@" + Node, timeoutSeconds: 2)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        using var doc = JsonDocument.Parse(answer);
        Assert.Equal("Dispatched", doc.RootElement.GetProperty("status").GetString());
        var activityPath = doc.RootElement.GetProperty("activityPath").GetString()!;

        // The budget is 2 s; the bound here only has to outlast it plus the activity write-back.
        var log = await Mesh.GetMeshNodeStream(activityPath)
            .Select(node => node.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l?.Status is ActivityStatus.Succeeded or ActivityStatus.Failed)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20)).Await();

        Assert.Equal(ActivityStatus.Failed, log!.Status);
        var lines = log.Messages.Select(m => m.Message).ToList();
        Assert.Contains(lines, l => l.Contains("within 2s", StringComparison.Ordinal)
                                    && l.Contains("preflight", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("cases passed", StringComparison.Ordinal));
    }
}
