using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The node-stream cache's FORCED terminals are delivered ONE AT A TIME (review of #7589).
///
/// <para>Disposing the cache ends every reader of every path in one synchronous sweep
/// (<c>Entry.EndReaders</c>). #7548 moved each reader's terminal off the turn that raised it, and a
/// pooled work item per terminal turns that sweep into N terminals delivered CONCURRENTLY. A consumer
/// that composes two node reads then receives them on two threads at once. That is the lock-order
/// deadlock <see cref="ReleaseLane"/> exists to prevent: Rx combinators take their gate on a terminal
/// and dispose their other sources under it, so a <c>Zip</c> nested in a <c>CombineLatest</c>
/// parks one release on each gate (pinned for owned connections by
/// <c>OwnedConnectionTest.ReleasingTwoConnectionsAConsumerComposes_NeverDeliversTheirTerminalsConcurrently</c>).</para>
///
/// <para>The test observes the overlap directly rather than staging the deadlock. Each reader's error
/// callback marks itself inside and holds an observation window. If the other reader's terminal
/// arrives inside that window, the two were delivered concurrently. On the shared release lane the
/// second cannot start until the first has returned, so the windows never overlap.</para>
/// </summary>
public class CacheTeardownTerminalsAreDeliveredOneAtATimeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task DisposingTheCache_EndsTwoReadersOneAfterTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var cache = Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = Mesh.JsonSerializerOptions;
        var pathA = $"{TestPartition}/teardown-a-{Guid.NewGuid():N}";
        var pathB = $"{TestPartition}/teardown-b-{Guid.NewGuid():N}";
        foreach (var path in new[] { pathA, pathB })
            await access.RunAsSystem(() => NodeFactory.CreateNode(
                    MeshNode.FromPath(path) with { Name = path, NodeType = "Markdown" }))
                .Should().Within(TestTimeouts.Convergence).Emit("the node must exist first", ct);

        var inside = 0;
        var overlapped = 0;
        var window = TimeSpan.FromSeconds(2);
        var ended = 0;
        var bothEnded = new AsyncSubject<Unit>();

        IDisposable Read(string path) => access.RunAsSystem(() => cache.GetStream(path, options))
            .Subscribe(
                _ => { },
                _ =>
                {
                    if (Interlocked.Increment(ref inside) > 1)
                        Volatile.Write(ref overlapped, 1);
                    // The observation window: a second terminal delivered concurrently arrives in here.
                    SpinWait.SpinUntil(() => Volatile.Read(ref overlapped) == 1, window);
                    Interlocked.Decrement(ref inside);
                    if (Interlocked.Increment(ref ended) == 2)
                    {
                        bothEnded.OnNext(Unit.Default);
                        bothEnded.OnCompleted();
                    }
                });

        using var a = Read(pathA);
        using var b = Read(pathB);
        // Both entries live before the sweep: each read has had its node.
        foreach (var path in new[] { pathA, pathB })
            await access.RunAsSystem(() => cache.GetStream(path, options))
                .Should().Within(TestTimeouts.Convergence).Emit($"{path} must be served before the sweep", ct);

        ((IDisposable)cache).Dispose(); // ONE sweep ends both readers

        await bothEnded.Should().Within(TestTimeouts.Convergence)
            .Emit("both readers must be told the cache is gone", ct);
        Volatile.Read(ref overlapped).Should().Be(0,
            "the terminals of one teardown sweep are delivered ONE AT A TIME — two at once deadlock a "
            + "consumer that composes the two reads (ReleaseLane)");
    }
}
