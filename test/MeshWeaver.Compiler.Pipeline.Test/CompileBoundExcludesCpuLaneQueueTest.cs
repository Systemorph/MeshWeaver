using System;
using System.Collections.Generic;
using System.Linq;
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
/// <para>Here the lane has ONE slot and leaves ahead of the compile keep it queued for longer than the
/// bound while the lane keeps draining. The compile must succeed once admitted. The bound's other
/// guarantees (a prefix that never queues, an admitted emit past the bound, a stalled lane) are pinned
/// by <see cref="CpuLaneBoundTest"/>.</para>
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

    [Fact(Timeout = 300_000)]
    public async Task ACompileQueuedLongerThanTheBound_BehindADrainingLane_IsNotTimedOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.CompileCpu);
        lane.Should().BeOfType<IoPool>().Which.MaxConcurrency.Should().Be(1,
            "the lane must have ONE slot, so the leaves ahead of the compile run one at a time");

        // Leaves ahead of the compile: each finishes well inside the stall window, together they
        // keep the compile queued for longer than the bound. A healthy, draining queue.
        var leafRun = Bound * 0.6;
        const int LeavesAhead = 3;
        var service = Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>();
        var queuedFor = TimeSpan.Zero;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        service.OnEmitStarting = _ => queuedFor = clock.Elapsed;
        try
        {
            var ahead = Enumerable.Range(0, LeavesAhead)
                .Select(_ => lane.InvokeBlocking(_ =>
                {
                    SpinWait.SpinUntil(() => false, leafRun);
                    return 0;
                }).Replay())
                .ToArray();
            var connections = ahead.Select(a => a.Connect()).ToArray();
            SpinWait.SpinUntil(() => ((IoPool)lane).CurrentlyWaiting >= LeavesAhead - 1, TimeSpan.FromSeconds(30))
                .Should().BeTrue("the leaves after the first must be queued before the compile is");

            clock.Restart();
            var result = await Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>()
                .CompileAndGetConfigurations(TypeNode(), SourceNodes())
                .Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit("a compile waiting in a draining queue must run once admitted", cancellationToken: ct);
            foreach (var c in connections) c.Dispose();

            result.Should().NotBeNull();
            result!.AssemblyLocation.Should().NotBeNullOrEmpty(
                "Roslyn produced an assembly — the compile was not timed out while it waited");
            queuedFor.Should().BeGreaterThan(Bound,
                "the compile must have waited for its slot LONGER than the bound, or this test proves nothing");
        }
        finally
        {
            service.OnEmitStarting = null;
        }
    }
}
