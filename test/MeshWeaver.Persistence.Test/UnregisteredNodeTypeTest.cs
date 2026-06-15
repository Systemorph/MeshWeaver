using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Asserts that activating a MeshNode whose <c>NodeType</c> is not registered
/// anywhere — no static <c>AddXxxType()</c> call, no persisted
/// <c>NodeTypeDefinition</c> node — fails FAST with a clear diagnostic, rather
/// than waiting 30s for a typeStream emission that will never come.
/// Regression guard for the PageLoadingTest ACME hang that traced back to
/// missing <c>AddSpaceType()</c>.
/// </summary>
public class UnregisteredNodeTypeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UnknownPath = "UnknownThing";
    private const string UnknownNodeType = "TotallyUnregisteredType";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Default node hub config — provides the AddData the error
            // overlay's per-node hub needs to activate. Without this the
            // overlay HubConfiguration falls back to a bare default that
            // fails Autofac resolution of IWorkspace.
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas())
            // Seed a MeshNode whose NodeType is not registered anywhere.
            // CreateNodeRequest would reject this via INodeValidator — using
            // AddMeshNodes bypasses that path and simulates the "stored node
            // whose type registration was later removed" scenario the probe
            // is meant to handle.
            .AddMeshNodes(MeshNode.FromPath(UnknownPath) with
            {
                Name = "Unknown Thing",
                NodeType = UnknownNodeType
            });

    /// <summary>
    /// Probe should fire within <c>NodeTypeProbeTimeout</c> (3s). Without the
    /// probe, the slow path waits <c>SlowPathTimeout = 30s</c> (stacked to 60s
    /// on double-enrichment) before falling through to the error overlay.
    /// Asserting the activation settles quickly proves the probe is the path
    /// that fires — any path that goes through the slow path would blow past
    /// this 8s budget.
    /// </summary>
    [Fact(Timeout = 8000)]
    public async Task UnknownNodeType_FailsFastNotAfterSlowPathTimeout()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var address = new Address(UnknownPath);
        var client = GetClient();

        // Attempt to ping the per-node hub. Either it succeeds (overlay
        // activated) or it surfaces a DeliveryFailure that the probe
        // produced. Either way, it must NOT hang for 30s+ on the slow path.
        try
        {
            await client.Observe(new PingRequest(), o => o.WithTarget(address))
                .Should().Within(TimeSpan.FromSeconds(6)).Emit();
        }
        catch (Exception ex)
        {
            Output.WriteLine($"Ping surfaced: {ex.GetType().Name}: {ex.Message}");
        }

        sw.Stop();
        Output.WriteLine($"Activation settled in {sw.ElapsedMilliseconds}ms");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6),
            "the existence probe should surface the missing NodeType in <3s — " +
            "anything close to SlowPathTimeout (30s) means the probe is bypassed");
    }
}
