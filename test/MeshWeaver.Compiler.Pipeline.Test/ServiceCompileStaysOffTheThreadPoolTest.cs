using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The PRODUCTION wiring of <c>Doc/Architecture/CompileOffTheThreadPool</c>: a compile reached through
/// the mesh's real <see cref="IMeshNodeCompilationService"/> must hand Roslyn a compilation with
/// <c>ConcurrentBuild</c> off, on a thread that is NOT a ThreadPool worker.
///
/// <para>Before the fix both were wrong on this path: <c>OnThreadPool(() =&gt; CompileAsync(...))</c>
/// binds the <c>Func&lt;Task&lt;T&gt;&gt;</c> overload (<c>Task.Run</c>), and the debug source write
/// (on by default) made the Roslyn half resume on a pool worker. <see cref="RoslynEmitStaysOffTheThreadPoolTest"/>
/// pins the emit's own behaviour; this pins that the service actually routes its emit there, which
/// a unit test of <see cref="CompileThread"/> alone cannot see.</para>
///
/// <para>The observation is made by the service's own seam, on the emit's thread, at the moment the
/// compilation is handed to Roslyn — a fact, not a timing.</para>
/// </summary>
public class ServiceCompileStaysOffTheThreadPoolTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/CompileOffThePoolProbe";

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "CompileOffThePoolProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type compiled once so the emit's thread and options can be observed",
            Configuration = "config => config.WithContentType<CompileOffThePoolProbe>()",
        },
    };

    private static IReadOnlyList<MeshNode> SourceNodes() =>
    [
        new MeshNode("probe", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "probe",
            State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public record CompileOffThePoolProbe { public string Title { get; init; } = \"\"; }",
            },
        },
    ];

    [Fact(Timeout = 300_000)]
    public async Task TheServicesEmit_RunsOnADedicatedThread_WithConcurrentBuildOff()
    {
        var service = Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>();
        var observed = 0;
        var onPoolThread = true;
        var concurrentBuild = true;
        service.OnEmitStarting = compilation =>
        {
            onPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            concurrentBuild = compilation.Options.ConcurrentBuild;
            Interlocked.Increment(ref observed);
        };
        try
        {
            var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
                .CompileAndGetConfigurations(TypeNode(), SourceNodes())
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);
            result.Should().NotBeNull();
            result!.AssemblyLocation.Should().NotBeNullOrEmpty("Roslyn produced an assembly");
        }
        finally
        {
            service.OnEmitStarting = null;
        }

        Volatile.Read(ref observed).Should().Be(1,
            "the seam must have seen the ONE emit this compile ran — otherwise the two readings below "
            + "are defaults, not observations");
        onPoolThread.Should().BeFalse(
            "the service's Roslyn half must run on a CompileThread, never on a ThreadPool worker the "
            + "silo's grain turns and routing legs need");
        concurrentBuild.Should().BeFalse(
            "the compilation the service emits must not fan out onto the ThreadPool either");
    }
}
