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
/// 🚨 Issue #7548, the follow-on of #7477: <b>a stream's terminal must never run its readers on
/// the turn that raises it.</b>
///
/// <para><b>What production showed.</b> After #7500 moved the subscribe verdict onto the stream's
/// own <c>sync/{id}</c> hub, that hub was the one that wedged. On memex-cloud's serving image,
/// <c>sync/qdl4…</c> sat at <c>RunLevel=Starting … Executing(DeliveryFailure, 2019462ms)</c>.
/// Its last line before the stall reports was <c>[SYNC_STREAM] OnError … Owner=Auth/_EaCredential/&lt;user&gt;</c>,
/// raised while a background sweep read credential nodes. The turn was inside
/// <c>SynchronizationStream.OnError</c>. That call delivers the terminal through the node-stream
/// cache's shared subject to every reader of the path, one after another and inline. So the first
/// reader whose error continuation did not return (the sweep runs its next steps inside that
/// continuation) held three things:</para>
/// <list type="bullet">
/// <item><description>every LATER reader of the same path. It is never told, because the fan-out
/// loop is parked on the reader in front of it.</description></item>
/// <item><description>the sync hub's own settle. <c>FailStartup</c> and <c>OpenGate</c> come
/// after the fan-out, so the hub stays at <c>Starting</c> with its gate closed.</description></item>
/// <item><description>the hub's teardown. Its <c>ShutdownRequest</c> queues FIFO behind the turn,
/// so it can never be disposed (<c>DISPOSAL DEADLOCK DETECTED</c> on the same
/// address).</description></item>
/// </list>
///
/// <para><b>The test.</b> Two readers subscribe to one absent node through the shared cache. The
/// first reader PARKS in its error callback. The park is the subject here: a volatile flag polled
/// under a bounded <see cref="SpinWait"/> and released in <c>finally</c>. While it is parked, the
/// second reader must still receive the same terminal. Before the fix the second reader sits
/// behind the first on the producing turn and hears nothing. After the fix each reader receives
/// the terminal on its own pooled continuation, and the producing turn returns.</para>
/// </summary>
public class AStreamTerminalNeverRunsReadersOnTheSyncHubTurnTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 240_000)]
    public async Task AReaderParkedInItsErrorPath_DoesNotHoldTheOtherReadersOfTheSameNode()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var workspace = Mesh.GetWorkspace();
        var path = $"{TestPartition}/_absent/shared-{Guid.NewGuid():N}";

        var parkedReaderEntered = new AsyncSubject<Unit>();
        var release = 0;
        var parkExpired = 0;

        // Reader 1 is the sweep. Its error continuation does not return while the flag is unset.
        using var parkedReader = access.RunAsSystem(() => workspace.GetMeshNodeStream(path))
            .Subscribe(
                _ => { },
                _ =>
                {
                    parkedReaderEntered.OnNext(Unit.Default);
                    parkedReaderEntered.OnCompleted();
                    if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence))
                        Volatile.Write(ref parkExpired, 1);
                });

        // Reader 2 is any other reader of the same node, subscribed before the verdict arrives.
        var secondReader = new AsyncSubject<string>();
        using var second = access.RunAsSystem(() => workspace.GetMeshNodeStream(path))
            .Subscribe(
                _ => { secondReader.OnNext("value"); secondReader.OnCompleted(); },
                ex => { secondReader.OnNext(ex.GetType().Name); secondReader.OnCompleted(); },
                () => { secondReader.OnNext("completed-empty"); secondReader.OnCompleted(); });
        try
        {
            await parkedReaderEntered.Should().Within(TestTimeouts.Convergence)
                .Emit("the absent node's verdict must reach the first reader's error path",
                    cancellationToken: TestContext.Current.CancellationToken);

            var answer = await secondReader.Should().Within(TestTimeouts.Quick)
                .Emit("a second reader of the SAME node must be told while the first reader is still "
                      + "parked in its error continuation (#7548). The terminal must not be fanned out "
                      + "inline on the turn that raised it",
                    cancellationToken: TestContext.Current.CancellationToken);

            answer.Should().Be(nameof(DeliveryFailureException),
                "the second reader receives the same NotFound verdict");
            Volatile.Read(ref parkExpired).Should().Be(0,
                "the second reader was answered while the first reader still held its error path");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }
}
