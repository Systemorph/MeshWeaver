using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Services.LanguageServer;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Every diagnostics request of the language service binds its compilation on the CPU lane
/// (<c>IoPoolNames.CompileCpu</c>): a dedicated, bounded thread — never a ThreadPool worker
/// (<c>Doc/Architecture/CompileOffTheThreadPool</c>).
///
/// <para>Before the lane these ran as <c>_ioPool.Run</c> leaves on the Compile pool, i.e. ON the
/// ThreadPool: the pool caps them at the processor count, which is exactly the number of workers
/// the grain turns have before injection starts — and the script workspace additionally bound with
/// Roslyn's default <c>ConcurrentBuild</c>, fanning out further. All three arms are covered — the
/// node workspace (<c>GetDiagnostics</c>), a speculative proposal against a NodeType, and a script
/// cell — because each had its own <c>GetDiagnostics</c> call.</para>
///
/// <para>The observation is the service's own seam, on the binding thread, at the moment it binds —
/// the thread's identity is a fact, not a timing.</para>
/// </summary>
public class LanguageServiceDiagnosticsRunOnTheCpuLaneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private MeshNodeLanguageService LanguageService =>
        (MeshNodeLanguageService)Mesh.ServiceProvider.GetRequiredService<IMeshLanguageService>();

    private sealed record Binding(bool OnPool, string? ThreadName);

    [Fact(Timeout = 300_000)]
    public async Task EveryDiagnosticsArm_BindsOnTheCpuLane_NeverOnTheThreadPool()
    {
        var bindings = new ConcurrentQueue<Binding>();
        var service = LanguageService;
        service.OnDiagnoseStarting = () => bindings.Enqueue(
            new Binding(Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name));
        try
        {
            var (typePath, sourcePath, typeName) = await ANodeTypeWithOneSource();

            // Arm 1 — a speculative proposal against a NodeType.
            await service.CheckSpeculativeOutcome(typePath, sourcePath,
                    $"public record {typeName} {{ public string Title {{ get; init; }} = string.Empty; }}")
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
            var afterSpeculative = bindings.Count;

            // Arm 2 — the node workspace.
            await service.GetDiagnostics(typePath)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
            var afterWorkspace = bindings.Count;

            // Arm 3 — a script cell (an owner that is not a NodeType).
            var scriptId = $"ScriptOwner{Guid.NewGuid():N}";
            await MeshService.CreateNode(new MeshNode(scriptId, "type")
            {
                Name = "a script owner",
                Content = new CodeConfiguration { Code = "not code", Language = "markdown" },
                State = MeshNodeState.Active,
            }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
            await service.CheckSpeculativeOutcome($"type/{scriptId}", $"type/{scriptId}/Source/Probe.cs", "var probe = 1 + 1;")
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

            afterSpeculative.Should().BeGreaterThan(0, "the speculative arm must bind through the seam");
            afterWorkspace.Should().BeGreaterThan(afterSpeculative, "the workspace arm must bind through the seam");
            bindings.Count.Should().BeGreaterThan(afterWorkspace, "the script arm must bind through the seam");
        }
        finally
        {
            service.OnDiagnoseStarting = null;
        }

        bindings.Where(b => b.OnPool).Should().BeEmpty(
            "a diagnostics bind is pure Roslyn CPU; on a ThreadPool worker it holds a thread the silo's "
            + "grain turns and routing legs need");
        bindings.Should().OnlyContain(b => b.ThreadName == "mw-cpu-lane",
            "the bind must run on the bounded CPU lane, not on some other dedicated thread");
    }

    private async Task<(string TypePath, string SourcePath, string TypeName)> ANodeTypeWithOneSource()
    {
        var id = $"LaneType{Guid.NewGuid():N}";
        var typePath = $"type/{id}";

        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition { Configuration = $"config => config.WithContentType<{id}>()" },
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        await MeshService.CreateNode(new MeshNode($"{id}.cs", $"{typePath}/Source")
        {
            NodeType = "Code",
            Name = $"{id}.cs",
            Content = new CodeConfiguration
            {
                Code = $"public record {id} {{ public string Id {{ get; init; }} = string.Empty; }}",
                Language = "csharp",
            },
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        return (typePath, $"{typePath}/Source/{id}.cs", id);
    }
}
