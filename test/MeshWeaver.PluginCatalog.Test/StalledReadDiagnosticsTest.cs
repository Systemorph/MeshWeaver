#pragma warning disable CS1591

using System;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the contract of <see cref="StalledReadDiagnostics"/> itself. It runs on the failure path of
/// issue #1405's post-install read, which means it runs at the exact moment the mesh is already
/// misbehaving — so a diagnostic that throws, or that waits longer than the failure it explains,
/// would REPLACE the evidence instead of producing it. That is not a hypothetical: the whole reason
/// this helper exists is that the one reproduction we have contains no distinguishing log line at
/// all, so the report is the only thing the next red run will carry.
/// </summary>
public class StalledReadDiagnosticsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    /// <summary>
    /// The worst input the helper can get: a path with nothing behind it — no durable row, no owner
    /// to answer, so BOTH probes fail. It must still return prose, inside its bound, naming the
    /// fork.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OnAPathWithNothingBehindIt_ItStillReportsInsideItsBudget()
    {
        var budget = TimeSpan.FromSeconds(3);
        var started = DateTimeOffset.UtcNow;

        var report = await StalledReadDiagnostics.Describe(Mesh, "NoSuchSpace/NoSuchNode", budget);

        Output.WriteLine(report);
        var elapsed = DateTimeOffset.UtcNow - started;

        report.Should().Contain("durable row: ABSENT",
            "resolving whether the row is durable is the fork the report exists to answer — an "
            + "absent row means the INSTALL, not the read, is the stalled stage");
        report.Should().Contain("cache-bypassing read:",
            "the second probe must always be reported, including when it is the one that failed");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "two probes at a {0}s budget must not out-live the read they explain", budget.TotalSeconds);
    }

    /// <summary>
    /// The happy path stays exactly the read it wraps: a node that IS there comes back, and nothing
    /// about the diagnostic is on that path.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AReadThatSucceeds_IsUntouched()
    {
        const string path = "DiagProbe/Node";
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .CreateNode(MeshNode.FromPath(path) with
            {
                Name = "Node",
                State = MeshNodeState.Active,
                Content = "content"
            })
            .Should().Emit();

        var node = await StalledReadDiagnostics.ReadOrExplain(
            Mesh, path, TimeSpan.FromSeconds(30), Output.WriteLine);

        node.Path.Should().Be(path);
    }
}
