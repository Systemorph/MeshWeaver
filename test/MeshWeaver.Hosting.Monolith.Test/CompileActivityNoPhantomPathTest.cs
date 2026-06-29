using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro + contract for the atioz <c>_Activity/compile-*</c> resubscribe / re-route storm.
///
/// <para><b>The storm.</b> <see cref="NodeTypeCompilationHelpers.RunCompile"/> used to create the
/// compile activity at <c>{nodeTypePath}/_Activity/compile-&lt;ts&gt;</c> <i>fire-and-forget</i>
/// (swallowing the create failure at Debug, with NO partition-provision ordering) and then stamp
/// <see cref="NodeTypeDefinition.LastCompilationActivityPath"/> on the NodeType <i>unconditionally</i>.
/// On a not-yet-provisioned partition (or any transient create fault) the activity node was never
/// created, yet the NodeType still advertised the phantom <c>compile-&lt;ts&gt;</c> path. Every reader
/// of that NodeType — the GUI compile-progress indicator, the Progress layout-area embed, the
/// per-NodeType hub's own activity watcher — then subscribed to that never-created path, each routing
/// a <c>SubscribeRequest</c> → <c>RoutingGrain</c> → endless <c>[ROUTE] NotFound</c> for a FEW specific
/// <c>compile-&lt;ts&gt;</c> paths, saturating routing and wedging the portal.</para>
///
/// <para><b>The fix.</b> The activity create is now provision-ordered + OBSERVED, and
/// <c>LastCompilationActivityPath</c> is stamped ONLY when the create actually landed — the NodeType
/// never advertises a node that does not exist. This class pins (1) the happy-path honesty contract
/// (a stamped path is a REAL, subscribable activity node) and (2) the storm-bound contract
/// (subscribing to a non-existent <c>_Activity/compile-*</c> path stays responsive — bounded, fast
/// OnError each time — and never wedges the hub).</para>
/// </summary>
public class CompileActivityNoPhantomPathTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// HONESTY CONTRACT (regression guard for the fix). A compile stamps
    /// <see cref="NodeTypeDefinition.LastCompilationActivityPath"/> only when the activity node was
    /// actually created — so a non-null stamp is ALWAYS a real, subscribable node, never a phantom
    /// the router will NotFound on. We drive a real compile, then prove the stamped path resolves to
    /// a real <see cref="ActivityLog"/> within a fast deadline (not a forever-cold subscribe / a
    /// NotFound fault).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CompiledNodeType_LastCompilationActivityPath_PointsAtAnExistingActivityNode()
    {
        var nodeTypePath = "type/PhantomGuardStory";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = "PhantomGuardStory",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<PhantomGuardStory>()"
            },
            State = MeshNodeState.Active
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration
                {
                    Code = "public record PhantomGuardStory { public string Title { get; init; } = string.Empty; }",
                    Language = "csharp"
                },
                State = MeshNodeState.Active
            }))
            .Should().Within(30.Seconds()).Emit();

        var node = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(40.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        var def = (NodeTypeDefinition)node.Content!;

        def.LastCompilationActivityPath.Should().NotBeNullOrEmpty(
            "a real compile in a provisioned mesh creates the activity, so the stamp is present");
        def.LastCompilationActivityPath!.Should().Contain("/_Activity/compile-");

        // The stamped path MUST resolve to a real activity node — NOT a phantom that would
        // NotFound-storm the router. A populated emission within a fast deadline proves it exists.
        var activity = await Mesh.GetMeshNodeStream(def.LastCompilationActivityPath!)
            .Where(n => n?.Content is ActivityLog)
            .Should().Within(15.Seconds())
            .Emit("the stamped LastCompilationActivityPath must point at a node that exists — "
                + "the fix stamps the path only after the activity create lands");
        (activity!.Content as ActivityLog)!.Category.Should().Be(ActivityCategory.Compilation);
    }

    /// <summary>
    /// STORM-BOUND CONTRACT. Subscribing to a NON-EXISTENT <c>_Activity/compile-*</c> path — the exact
    /// shape the phantom-path bug produced, and the shape a stale/GC'd reference still produces — must
    /// stay responsive: each read surfaces a fast <see cref="DeliveryFailureException"/> (the router's
    /// NotFound) rather than re-routing unbounded, and the mesh hub stays live throughout. This proves
    /// that even when the storm-prone path is hit on a tight loop the cascade is bounded and the hub is
    /// never wedged.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SubscribingAbsentCompileActivity_InTightLoop_StaysResponsive_NoStorm()
    {
        var nodeTypePath = "type/AbsentActivityStory";
        // A never-created compile activity under a real, provisioned NodeType partition — exactly the
        // phantom shape: the closest routable ancestor exists, the leaf does not.
        var absentActivityPath = $"{nodeTypePath}/_Activity/compile-{Guid.NewGuid():N}";

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsSystem())
        {
            for (var i = 0; i < 12; i++)
            {
                var note = await Mesh.GetMeshNodeStream(absentActivityPath)
                    .Where(n => n?.Content is not null)
                    .Materialize()
                    .Should().Within(TimeSpan.FromSeconds(5)).Match(
                        n => n.Kind == NotificationKind.OnError,
                        $"absent compile-activity read #{i} must surface a fast NotFound, never hang or re-route unbounded");
                note.Exception.Should().BeOfType<DeliveryFailureException>(
                    $"read #{i} must keep surfacing the routing NotFound (the storm breaker replays it), never change shape");
            }
        }

        // The mesh hub stayed responsive throughout the loop: a real create still completes fast.
        var liveProbePath = $"{TestPartition}/{Guid.NewGuid().AsString()}";
        await MeshService.CreateNode(MeshNode.FromPath(liveProbePath) with
        {
            Name = "post-storm liveness probe",
            NodeType = "Markdown",
            MainNode = TestPartition,
            State = MeshNodeState.Active
        }).Should().Within(15.Seconds()).Emit(
            "after a tight loop of absent-activity reads the mesh must still service a normal create — "
            + "proving routing was never saturated by an unbounded NotFound storm");
    }
}
