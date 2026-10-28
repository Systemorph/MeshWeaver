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
/// 🚨 Issues #7492 / #7495 / #7477: <b>a reader of one stream must never be able to stop every
/// other read in the process.</b>
///
/// <para><b>What production showed.</b> On three memex-cloud pods the node-stream cache hub
/// (<c>cache/{meshId}</c>, the ONE hub through which every node read of the process subscribes)
/// sat at <c>Executing(DeliveryFailure, …)</c> for 16 to 56 minutes with 6,000–9,000 deliveries
/// queued behind it, and every later <c>SubscribeRequest</c> — a chat composer, a document view, a
/// Space root — timed out with its trail ending <c>QUEUED queue=main</c>. The pod logs pin the turn:
/// each wedge began right after a <c>[ROUTE] NotFound</c> for a credential node a background sweep
/// read, and the last line the pod logged for it was <c>[SYNC_STREAM] OnError … Owner=…</c> — i.e.
/// the cache hub was still inside <c>SynchronizationStream.OnError</c> for that read.</para>
///
/// <para><b>Why the cache hub was there at all.</b> A remote stream's DATA is applied on the stream's
/// own <c>sync/{id}</c> hub (<c>RouteStreamMessage</c> forwards every <c>DataChangedEvent</c> to it),
/// so a reader that is slow or blocked on a value holds up that one stream. Its FAULT was not: the
/// subscribe verdict is the answer to <c>hub.Observe(SubscribeRequest)</c>, issued by the
/// SUBSCRIBING hub, so <c>HandleCallbacks</c> raised it on that hub's turn and
/// <c>JsonSynchronizationStream</c> called <c>reduced.OnError</c> right there — running every
/// reader's error continuation synchronously on the cache hub's single action block. Any reader that
/// blocked in its error path therefore blocked all reads of the process.</para>
///
/// <para><b>The test.</b> A reader of an absent node PARKS in its error callback (the park is the
/// subject: a volatile flag polled under a bounded <see cref="SpinWait"/>, released in
/// <c>finally</c>). While it is parked, an unrelated read of a second absent node must still be
/// answered — its <c>SubscribeRequest</c> is posted by the same cache hub. Before the fix the cache
/// hub is the parked thread, the second read's subscribe is queued behind it, and the read times
/// out (the production shape). After it, the verdict reaches the stream on its own hub, and the
/// cache hub keeps serving.</para>
/// </summary>
public class ASubscribeVerdictNeverRunsReadersOnTheCacheHubTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 240_000)]
    public async Task AReaderParkedInItsErrorPath_DoesNotStopOtherReads()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.GetWorkspace();
        var parkedPath = $"{TestPartition}/_absent/parked-{Guid.NewGuid():N}";
        var probePath = $"{TestPartition}/_absent/probe-{Guid.NewGuid():N}";

        var parkedReaderEntered = new AsyncSubject<Unit>();
        var release = 0;
        var parkExpired = 0;

        using var parkedReader = access.RunAsSystem(() => workspace.GetMeshNodeStream(parkedPath).Take(1))
            .Subscribe(
                _ => { },
                _ =>
                {
                    parkedReaderEntered.OnNext(Unit.Default);
                    parkedReaderEntered.OnCompleted();
                    // The reader that blocks in its error path — what production's sweep did, for a
                    // reason that lies outside the cache. Bounded so a regression cannot hang the run.
                    if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence))
                        Volatile.Write(ref parkExpired, 1);
                });
        try
        {
            await parkedReaderEntered.Should().Within(TestTimeouts.Convergence)
                .Emit("the absent node's read must reach its reader's error path",
                    cancellationToken: TestContext.Current.CancellationToken);

            var probe = await access.RunAsSystem(() => workspace.GetMeshNodeStream(probePath).Take(1))
                .Select(_ => "value")
                .Catch((Exception ex) => Observable.Return(ex.GetType().Name))
                .DefaultIfEmpty("completed-empty")
                .Should().Within(TestTimeouts.Quick)
                .Emit("a read of an unrelated node is subscribed through the same cache hub; a reader "
                      + "parked in ANOTHER stream's error path must not hold that hub (#7492)",
                    cancellationToken: TestContext.Current.CancellationToken);

            probe.Should().Be(nameof(DeliveryFailureException),
                "the unrelated absent node is answered with its own NotFound verdict");
            Volatile.Read(ref parkExpired).Should().Be(0,
                "the probe was answered while the parked reader still held its error path");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }
}
