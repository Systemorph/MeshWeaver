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
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// The <c>roslyn-compile</c> bound (<see cref="CompilationCacheOptions.RoslynCompileTimeout"/>) times the
/// compile's OWN work, never its wait for a slot on the CPU lane (<see cref="IoPoolNames.CompileCpu"/>).
///
/// <para>The lane is capped at the processor count, so a burst of compiles queues. With the clock
/// running from subscription, a compile still QUEUED when the bound elapsed failed terminally as a hung
/// leg although it had never started: on a CI runner the combo verification of one package
/// dispatched 47 types together, and the ten still queued after five minutes were parked at
/// <c>CompilationStatus=Error</c>, which read as a red package (#4413).</para>
///
/// <para>Here the lane has ONE slot, a parked leaf holds it for several bounds, and a compile queues
/// behind it. The compile must neither settle while it is queued nor fail once admitted. The parked
/// leaf is released in a <c>finally</c>, so a failing assertion cannot strand the lane.</para>
/// </summary>
public class CompileBoundExcludesCpuLaneQueueTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/LaneQueueProbe";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IoPoolOptions>();
                services.AddSingleton(new IoPoolOptions { CompileCpu = 1 });
                return services.Configure<CompilationCacheOptions>(o => o.RoslynCompileTimeout = Bound);
            });

    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "LaneQueueProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = DateTimeOffset.UtcNow,
        Content = new NodeTypeDefinition
        {
            Description = "a type compiled behind a saturated CPU lane",
            Configuration = "config => config.WithContentType<LaneQueueProbe>()",
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
                Code = "public record LaneQueueProbe { public string Title { get; init; } = \"\"; }",
            },
        },
    ];

    private sealed class Park
    {
        public volatile int Release;
        public volatile int Running;
    }

    [Fact(Timeout = 300_000)]
    public async Task ACompileQueuedLongerThanTheBound_IsNotTimedOut_AndCompilesOnceAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.CompileCpu);
        lane.Should().BeOfType<IoPool>().Which.MaxConcurrency.Should().Be(1,
            "the lane must have ONE slot, so the parked leaf alone saturates it");

        var park = new Park();
        var settled = 0;
        Exception? fault = null;
        try
        {
            var parked = lane.InvokeBlocking(_ =>
            {
                park.Running = 1;
                SpinWait.SpinUntil(() => park.Release != 0, TimeSpan.FromSeconds(120));
                return 0;
            }).Replay();
            using var parkedConnection = parked.Connect();
            SpinWait.SpinUntil(() => park.Running != 0, TimeSpan.FromSeconds(30))
                .Should().BeTrue("the parked leaf must hold the lane's only slot before the compile starts");

            var compile = Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
                .CompileAndGetConfigurations(TypeNode(), SourceNodes())
                .Take(1)
                .Replay();
            using var compileConnection = compile.Connect();
            using var watch = compile.Subscribe(
                _ => Interlocked.Exchange(ref settled, 1),
                ex => { fault = ex; Interlocked.Exchange(ref settled, 1); });

            SpinWait.SpinUntil(() => ((IoPool)lane).CurrentlyWaiting > 0, TimeSpan.FromSeconds(60))
                .Should().BeTrue("the compile's CPU half must be QUEUED behind the parked leaf — "
                    + "otherwise the hold below measures nothing");

            // Negative check, bounded: hold the lane for three bounds. Before the fix the compile
            // failed with a roslyn-compile TimeoutException after ONE bound while still queued.
            SpinWait.SpinUntil(() => Volatile.Read(ref settled) != 0, Bound * 3)
                .Should().BeFalse("a compile that is only WAITING for a CPU-lane slot must not be "
                    + $"timed out (fault: {fault?.Message ?? "none"})");

            park.Release = 1;
            var result = await compile
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the compile must run once the lane admits it", cancellationToken: ct);
            result.Should().NotBeNull();
            result!.AssemblyLocation.Should().NotBeNullOrEmpty("Roslyn produced an assembly");
        }
        finally
        {
            park.Release = 1;
        }
    }
}
