using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the SINGLE-COMPILE-DRIVER invariant behind the compile-heavy 2-core CI
/// flake (MeshNodeCompilationIntegrationTest / OrleansDynamicCompilationTest /
/// FrameworkStaleInstanceRenderTest rotating 60s timeouts).
///
/// <para><b>The defect this ratchets against:</b> on a FRESH dynamic NodeType
/// (CompilationStatus still null) the <c>GetCompilationPathRequest</c> handler
/// treated <c>null</c> as "settled", raced past <c>AwaitCompilationSettled</c>,
/// and ran Roslyn INLINE — concurrently with the first-build kickoff's
/// watcher-dispatched activity compile of the SAME NodeType. Under 2-core
/// contention the loser read the winner's DLL mid-emit ("Failed to load
/// assembly … the build is not usable"), deleted it, and terminal-wrote
/// <c>Error</c>; the surviving handler write-back then stamped <c>Ok</c> +
/// assembly refs but NOT <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>
/// — the wedge signature <c>Ok + LatestAssemblyPath set + fv=''</c> that makes
/// <c>HasUsableBuild</c> false forever (nothing re-triggers: the kickoff needs
/// status null). The fix makes the handler DISPATCH through the status control
/// plane (flip Pending, watcher drives the one compile) and wait, exactly like
/// <c>HandleCreateRelease</c>'s <c>DispatchPendingFlip</c>; and makes every
/// fresh-compile success write COMPLETE (framework version stamped).</para>
///
/// <para>Post-fix these assertions are DETERMINISTIC (the response can only be
/// posted after the single compile's terminal write); pre-fix they flake under
/// contention — the same population CI's 2-vCPU runners sample every merge.</para>
/// </summary>
public class CompileSingleDriverConsistencyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IObservable<MeshNode> CreateNodeType(string nodeTypeId)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{nodeTypeId}>()"
            },
            State = MeshNodeState.Active
        };
        return MeshService.CreateNode(typeNode)
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
            {
                NodeType = "Code",
                Name = "code",
                Content = new CodeConfiguration
                {
                    Code = $$"""
                        public record {{nodeTypeId}}
                        {
                            public string Id { get; init; } = string.Empty;
                        }
                        """,
                    Language = "csharp"
                },
                State = MeshNodeState.Active
            }));
    }

    private IObservable<GetCompilationPathResponse> Compile(string nodeTypePath) =>
        Mesh.Observe(
                (IRequest<GetCompilationPathResponse>)new GetCompilationPathRequest(),
                o => o.WithTarget(new Address(nodeTypePath)))
            .Select(d => d.Message);

    /// <summary>
    /// A successful first compile must leave the NodeType in a CONSISTENT,
    /// usable-build terminal state — Status=Ok AND assembly refs AND a
    /// non-empty CompiledFrameworkVersion — never the half-written
    /// Ok-with-empty-framework-version wedge the double-driver race produced.
    /// </summary>
    [Fact]
    public async Task FreshCompile_TerminalStateIsComplete()
    {
        var nodeTypeId = $"SingleDriver{Guid.NewGuid():N}";
        var nodeTypePath = $"type/{nodeTypeId}";

        await CreateNodeType(nodeTypeId).Should().Within(30.Seconds()).Emit();

        var response = await Compile(nodeTypePath).Should().Within(60.Seconds()).Emit();
        response.Success.Should().BeTrue($"first compile should succeed; error: {response.Error}");
        response.AssemblyLocation.Should().NotBeNullOrEmpty();

        // The response is only posted AFTER the terminal write (single driver:
        // the handler waits for the dispatched compile to settle, then hydrates),
        // so the own node must ALREADY be complete — no separate compile can be
        // in flight to "finish" it later.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Should().Within(30.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);

        var def = (NodeTypeDefinition)node.Content!;
        def.CompilationError.Should().BeNull();
        def.LatestAssemblyPath.Should().NotBeNullOrEmpty(
            "the terminal write must carry the durable assembly reference");
        def.LatestAssemblyCollection.Should().NotBeNullOrEmpty();
        def.CompiledFrameworkVersion.Should().NotBeNullOrEmpty(
            "a successful compile's terminal write must stamp the framework version — "
            + "Ok with an empty CompiledFrameworkVersion is the double-driver wedge state "
            + "(HasUsableBuild=false forever, nothing re-triggers)");
    }

    /// <summary>
    /// Two concurrent GetCompilationPathRequests on the SAME fresh NodeType must
    /// both succeed off the ONE dispatched compile — the status field is the
    /// single-flight lock; neither request may drive a second Roslyn run that
    /// races the first on the shared cache DLL.
    /// </summary>
    [Fact]
    public async Task ConcurrentRequests_OnFreshNodeType_BothSucceedConsistently()
    {
        var nodeTypeId = $"SingleDriverPair{Guid.NewGuid():N}";
        var nodeTypePath = $"type/{nodeTypeId}";

        await CreateNodeType(nodeTypeId).Should().Within(30.Seconds()).Emit();

        var both = await Observable.Zip(Compile(nodeTypePath), Compile(nodeTypePath))
            .Should().Within(90.Seconds()).Emit();

        foreach (var response in both)
        {
            response.Success.Should().BeTrue(
                $"every concurrent requester must be served off the single compile; error: {response.Error}");
            response.AssemblyLocation.Should().NotBeNullOrEmpty();
        }

        // Same terminal-consistency bar as the single-request case.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(nodeTypePath)
            .Should().Within(30.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok);
        var def = (NodeTypeDefinition)node.Content!;
        def.CompiledFrameworkVersion.Should().NotBeNullOrEmpty();
        def.LatestAssemblyPath.Should().NotBeNullOrEmpty();
    }
}
