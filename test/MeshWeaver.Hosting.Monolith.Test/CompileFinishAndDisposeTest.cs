using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// Repro + contract for the "compile did not settle within 30s → instance
/// renders the fallback overlay" bug.
///
/// <para>The architecture the bug violated (per the design directive):</para>
/// <list type="number">
///   <item>The compile <b>trigger</b> returns as soon as the Activity exists —
///     it does not block the caller waiting for Roslyn. The activity hub owns
///     the work (<see cref="NodeTypeCompileActivityHandler"/> posts
///     <c>RunCompileResponse(Dispatched: true)</c> immediately).</item>
///   <item>The <b>activity</b> updates the NodeType MeshNode when it has
///     finished (terminal <c>WriteToParent</c> Status=Ok + assembly fields) and
///     marks its own ActivityLog <see cref="ActivityStatus.Succeeded"/> with an
///     <c>End</c> timestamp — the signal that it finished generation and the
///     activity hub is free to dispose/deactivate.</item>
///   <item>Consumers (instance activation, the GUI Progress area) observe the
///     finished state reactively via <c>stream.Where(settled)</c> — they do not
///     hard-fail at a short deadline. The give-up budget is capped at 60s and
///     is a safety bound only; a longer wait never rescues a wedged grain.</item>
/// </list>
///
/// <para>This test drives a compile and asserts it <b>finishes generation</b>
/// (assembly produced, NodeType settles to Ok) and the activity reaches its
/// <b>terminal / auto-dispose</b> state (Succeeded + End stamped, never left
/// Running). A regression to the stuck-compile behaviour would either leave the
/// activity <c>Running</c> with <c>End == null</c> or never produce an
/// assembly.</para>
/// </summary>
public class CompileFinishAndDisposeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Create a NodeType + its Source Code nodes, then trigger a compile via
    /// <see cref="GetCompilationPathRequest"/> and return the response. The
    /// request resolves through the per-NodeType hub's compile pipeline — the
    /// same path the GUI / instance activation drives — and carries the
    /// activity <see cref="ActivityLog"/> back so we can assert the terminal
    /// state without a separate subscription.
    /// </summary>
    private IObservable<GetCompilationPathResponse> CreateAndCompile(
        string nodeTypeId,
        NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var nodeTypePath = $"type/{nodeTypeId}";
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypeId,
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active
        };

        return MeshService.CreateNode(typeNode)
            .SelectMany(_ => sources
                .Select(source => MeshService.CreateNode(new MeshNode(source.Name, $"{nodeTypePath}/Source")
                {
                    NodeType = "Code",
                    Name = source.Name,
                    Content = new CodeConfiguration { Code = source.Code, Language = "csharp" },
                    State = MeshNodeState.Active
                }))
                .Aggregate(Observable.Return<MeshNode?>(null), (chain, next) =>
                    chain.SelectMany(_ => next.Select(n => (MeshNode?)n))))
            .SelectMany(_ => Mesh.Observe(
                    new GetCompilationPathRequest(),
                    o => o.WithTarget(new Address(nodeTypePath))))
            .Select(d => d.Message);
    }

    [Fact(Timeout = 60_000)]
    public async Task TriggeredCompile_FinishesGeneration_AndActivityReachesTerminalDisposedState()
    {
        var response = await CreateAndCompile("FinishStory",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<FinishStory>()" },
            ("code", "public record FinishStory { public string Title { get; init; } = string.Empty; }"))
            .Should().Within(55.Seconds()).Emit();

        // Finished generation: the compile produced an assembly. This is the
        // "activity updates the NodeType when it has finished" half of the
        // contract — Success comes from a settled Ok with a real assembly path.
        response.Success.Should().BeTrue($"the triggered compile must finish; error: {response.Error}");

        response.Log.Should().NotBeNull("every compile is a proper Activity with a log");

        // Terminal / auto-dispose: a finished activity is Succeeded with an End
        // timestamp. The bug left it stuck (Status stays Running, End null) so
        // every instance fell through to the 30s "compile did not settle"
        // overlay. Asserting Succeeded + End locks in that the activity actually
        // completes and is free to deactivate.
        response.Log!.Status.Should().Be(ActivityStatus.Succeeded,
            "the compile activity must reach a terminal Succeeded status — finished generation");
        response.Log.Status.Should().NotBe(ActivityStatus.Running,
            "the activity must not be left Running (the wedged-compile symptom)");
        response.Log.End.Should().NotBeNull(
            "a finished activity stamps End — the signal it completed and the hub can dispose");
    }

    [Fact(Timeout = 60_000)]
    public async Task FailedCompile_StillReachesTerminalDisposedState_NotStuck()
    {
        // Even a broken source must not wedge the activity: it finishes Failed
        // (with End stamped) rather than hanging Running until a deadline. The
        // consumer then sees a settled Error via stream.Where and renders the
        // diagnostic — not the "did not settle" timeout overlay.
        var response = await CreateAndCompile("FinishBroken",
            new NodeTypeDefinition { Configuration = "config => config.WithContentType<FinishBroken>()" },
            ("code", "public record FinishBroken { this is not valid C# }"))
            .Should().Within(55.Seconds()).Emit();

        response.Success.Should().BeFalse("invalid source must fault the compile");
        response.Log.Should().NotBeNull();
        response.Log!.Status.Should().Be(ActivityStatus.Failed,
            "a broken compile must reach terminal Failed — not stay Running");
        response.Log.End.Should().NotBeNull(
            "even a failed activity stamps End and disposes — it must never wedge");
    }
}
