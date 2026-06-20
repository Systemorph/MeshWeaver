using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🅿️ Failure ISOLATION + VISIBILITY for NodeType compile failures (the recompile-storm
/// wedge cure).
///
/// <para>A NodeType whose source does not compile must NOT be able to wedge the portal: in
/// the stateless, Release-based compile model a broken type that kept re-running Roslyn on
/// every activation / Pending flip would saturate its per-NodeType hub's single-threaded
/// action block. The fix PARKS the failure (bounded + terminal — the type stops
/// recompiling) and NOTIFIES (a bell-databound Notification carrying the type path + error).
/// This test pins all four properties:</para>
/// <list type="bullet">
///   <item>(a) the type lands in the terminal <b>parked</b> state, after exactly ONE compile;</item>
///   <item>(b) a <b>Notification</b> is emitted carrying the type path + error;</item>
///   <item>(c) the hub stays <b>responsive</b> — a healthy node answers promptly afterward;</item>
///   <item>(d) <b>no storm</b> — Roslyn runs exactly once across many activations / enrichments.</item>
/// </list>
/// </summary>
public class NodeTypeCompileParkTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "ParkTest";
    private const string NodeTypeId = "ParkBroken";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";

    [Fact(Timeout = 120_000)]
    public async Task BrokenNodeType_Parks_Notifies_StaysBounded_AndKeepsHubResponsive()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = Mesh.GetWorkspace();
        var parkRegistry = Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

        // 1. A NodeType whose Configuration string is NOT valid C# — the first-build kickoff
        //    compile fails deterministically and the type settles at Error.
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Park Broken Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType (park test).",
                Configuration = "config => this is not valid C# at all ((await ("
            }
        }).Should().Emit();

        // Wait for the compile to settle at Error (cold Roslyn compile budget).
        await workspace.GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("NodeType compile settled at Error.");

        // (a) PARKED terminal state, after EXACTLY ONE compile.
        parkRegistry.IsParked(NodeTypePath)
            .Should().BeTrue("a deterministic compile error must park the type");
        parkRegistry.GetCompileAttemptCount(NodeTypePath)
            .Should().Be(1, "the broken type must compile exactly once before parking");

        // (b) NOTIFY — a bell-databound Notification was emitted carrying the type path + error.
        var notification = await WaitForFailureNotificationAsync(NodeTypePath, ct);
        notification.Should().NotBeNull("a parked type must emit a user-visible notification");
        var content = notification!.Content.Should().BeOfType<Notification>().Subject;
        content.TargetNodePath.Should().Be(NodeTypePath, "the bell must navigate to the failing type");
        content.Message.Should().Contain(NodeTypePath, "the notification must name the failing type");
        content.Title.Should().Contain("failed to compile");
        Output.WriteLine("Failure notification emitted.");

        // (d) NO STORM — hammer the activation/enrichment path 25× on an instance of the
        //     broken type (exactly the churn that storms a faulted compile cache). Roslyn must
        //     NOT run again: the parked type serves its cached error without recompiling.
        var instance = new MeshNode("park-instance", Partition)
        {
            Name = "Park Instance",
            NodeType = NodeTypePath,
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(instance).Should().Emit();

        var resolver = Mesh.ServiceProvider.GetRequiredService<INodeConfigurationResolver>();
        for (var i = 0; i < 25; i++)
        {
            var enriched = await resolver.ResolveConfiguration(instance)
                .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            enriched.Should().NotBeNull();
        }
        parkRegistry.GetCompileAttemptCount(NodeTypePath)
            .Should().Be(1, "after parking, 25 further enrichments must NOT re-run Roslyn");
        parkRegistry.IsParked(NodeTypePath)
            .Should().BeTrue("the type must still be parked after the hammer");
        Output.WriteLine("25 enrichments — still exactly one compile (no storm).");

        // (c) HUB STAYS RESPONSIVE — a healthy node's hub answers a Ping promptly (not wedged).
        const string healthyPath = $"{Partition}/healthy-doc";
        await NodeFactory.CreateNode(new MeshNode("healthy-doc", Partition)
        {
            Name = "Healthy Doc",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        }).Should().Emit();

        var client = GetClient();
        await client.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(new Address(healthyPath)))
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine("Healthy node answered Ping — the mesh is not wedged.");
    }

    /// <summary>
    /// Reactively polls for the failure Notification (no fixed sleep): the satellite write is
    /// driven by a fire-and-forget Subscribe inside the compile continuation, so it lands
    /// shortly after the compile settles. Filters by the failing type's path so it is robust
    /// to the recipient namespace.
    /// </summary>
    private async Task<MeshNode?> WaitForFailureNotificationAsync(string nodeTypePath, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return await Observable.Interval(TimeSpan.FromMilliseconds(150))
            .StartWith(0L)
            .SelectMany(_ => meshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{NotificationNodeType.NodeType}"))
                .Take(1))
            .Select(result => result.Items.FirstOrDefault(n =>
                n.Content is Notification notif && notif.TargetNodePath == nodeTypePath))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask(ct);
    }
}
