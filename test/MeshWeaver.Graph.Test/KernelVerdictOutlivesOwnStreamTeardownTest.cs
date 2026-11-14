using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The interleaving behind the intermittent <see cref="KernelHostDisposedMidRunFailsTheRunTest"/>
/// failure ("still Running after 00:01:48: its kernel host was disposed while the script was
/// parked"), forced every time.
///
/// <para><b>What happens.</b> When the Activity hub is disposed, the kernel executor fails the parked
/// run at the first instant of teardown and writes the Failed verdict through the hub's own-node
/// stream. That write is an <c>UpdateStreamRequest</c> queued on the MeshNode STORE's <c>sync/</c>
/// hub. The persistence sampler, and the dispose-time flush that reads it, learn about a commit only
/// when the REDUCED own-node stream — on a different <c>sync/</c> hub — emits it. The host's
/// DisposeHostedHubs phase disposes both hubs in parallel, so when the store's hub is scheduled
/// late (a loaded CI runner), the reduced stream's hub is gone before the verdict commits: the store
/// holds Failed, the flush writes the older Running state, and the fresh activation serves Running
/// for ever.</para>
///
/// <para><b>How it is forced.</b> The test parks the store hub's action block on a turn of its own
/// before the recycle, waits until the reduced stream's hub has finished disposing, and only then
/// releases the store: the verdict commits after the reduced stream is gone, on every run. The
/// release is a volatile flag polled under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>
/// and written in a <c>finally</c>, so a failing assertion cannot strand the parked turn.</para>
///
/// <para>Negative control: <see cref="KernelHostDisposedMidRunFailsTheRunTest"/> runs the same
/// scenario without the park.</para>
/// </summary>
public class KernelVerdictOutlivesOwnStreamTeardownTest(ITestOutputHelper output) : KernelRunSettlementTestBase(output)
{
    private int released;

    /// <summary>
    /// The verdict commits to the store after the reduced own-node stream's hub is gone. It must still
    /// be what the fresh activation serves: Failed, saying the host shut down. Before the fix the
    /// dispose-time flush wrote the state the reduced stream had last emitted — Running.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AVerdictCommittedAfterTheOwnStreamHubIsGone_IsStillPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = await CreateGate(ct);
        try
        {
            var activity = await Run(await CreateCell(WaitsForGate(gate), ct), ct);
            await AtTheGate(activity, ct);

            var activityHub = Mesh.GetHostedHub(new Address(activity), HostedHubCreation.Never);
            activityHub.Should().NotBeNull("the run's Activity hub hosts the kernel and must be live at the gate");
            var workspace = activityHub!.GetWorkspace();
            var store = workspace.DataContext.GetDataSourceForType(typeof(MeshNode))!.GetStreamForPartition(null)!;
            var ownStreamHub = workspace.GetStream(new MeshNodeReference())!.Hub;
            ownStreamHub.Should().NotBeSameAs(store.Hub,
                "the scenario needs the reduced own-node stream and the store on different hubs");

            // Park the store's action block on a turn of our own. Everything posted to it afterwards —
            // the kernel's Failed verdict among it — queues behind this turn.
            var parked = new AsyncSubject<Unit>();
            // A null change is the stream's own "nothing to apply": the turn exists only to hold the hub.
            store.Update(_ =>
            {
                parked.OnNext(Unit.Default);
                parked.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, Bound);
                return (ChangeItem<EntityStore>?)null;
            }, _ => { });
            await parked.Should().Within(Bound).Emit("the store's hub must be parked before its host is recycled");

            var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
            var recycled = access
                .RunAsSystem(() => RequestHub.RecycleNode(activity,
                    reason: "KernelVerdictOutlivesOwnStreamTeardownTest: the store commits the verdict after the own stream is gone"))
                .FirstAsync()
                .Replay(1);
            using var recycling = recycled.Connect();

            // The host's DisposeHostedHubs phase tears the reduced stream's hub down while the store is
            // still parked with the verdict queued behind it.
            await ownStreamHub.DisposalCompleted.Should().Within(Bound).Emit(
                "the reduced own-node stream's hub must be gone before the store commits the verdict");
            Volatile.Write(ref released, 1);

            await recycled.Await(ct);

            var log = await Terminal(activity, "the verdict committed to the store after the own stream's hub was gone", ct);
            log.Status.Should().Be(ActivityStatus.Failed, "the store committed the Failed verdict before the hub shut down");
            SaysTheHostShutDown(log).Should().BeTrue("the reader must see WHY the run ended");
        }
        finally
        {
            Volatile.Write(ref released, 1);
            await OpenGate(gate, CancellationToken.None);
        }
    }
}
