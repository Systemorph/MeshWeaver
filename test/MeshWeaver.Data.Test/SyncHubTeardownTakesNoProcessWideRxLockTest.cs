using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Data.TestDomain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// The turns of hosted <c>sync/{id}</c> hubs — their teardown, the writes they carry and the per-revision read
/// admission they run — must not take Rx's process-wide <c>SystemClock</c> registration lock
/// (MeshWeaver#5799, MeshWeaver#5388).
///
/// <para><b>The production signature.</b> #5799's verdict, <c>THE PUMP IS NOT TURNING</c>, is printed for
/// hosted <c>sync/*</c> hubs whose drain was handed to <c>TaskScheduler.Default</c> and never started. The
/// 2026-11-11 09:27:09Z <c>[POOLSTALL]</c> capture taken 6 s after such a burst had 14 of 41 pool workers in
/// <c>Monitor.Enter_Slowpath</c> inside <c>SystemClock.Register</c>, each inside a hub turn. Rx's
/// <c>StartWith(value)</c> and its <c>Merge</c> forms create an <c>AsyncLockScheduler</c> per subscription, and
/// each one registers under that ONE lock (see <see cref="InlineRxOperators"/>). A worker parked there runs no
/// other hub's drain, so those drains sit unstarted. #8140 took the lock out of <c>MessageHub</c>'s own
/// teardown, and <c>HubPathsTakeNoProcessWideRxLockTest</c> pins that for a bare hub. This class pins it for
/// the hubs the verdict was actually about: <c>sync/*</c> hubs, on the turns they take.</para>
///
/// <para><b>How.</b> A dedicated thread HOLDS the lock while the subject runs (the park is the subject; it is
/// released in a <c>finally</c>). The negative control is Rx's own <c>StartWith</c>, which must not complete
/// under the hold, so a green run cannot come from a hold that did not take effect.</para>
/// </summary>
public class SyncHubTeardownTakesNoProcessWideRxLockTest(ITestOutputHelper output) : HubTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(src => src
                .WithType<BusinessUnit>(t => t.WithInitialData(TestData.BusinessUnits))
                .WithType<LineOfBusiness>(t => t.WithInitialData(TestData.LinesOfBusiness))));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddData(data => data.AddHubSource(CreateHostAddress(),
                ds => ds.WithType<BusinessUnit>().WithType<LineOfBusiness>()));

    /// <summary>The lock <c>SystemClock.Register</c> takes: its static set of registered schedulers.</summary>
    private static object SystemClockRegistrationLock()
    {
        var systemClock = typeof(Observable).Assembly.GetType("System.Reactive.PlatformServices.SystemClock", throwOnError: true)!;
        var set = systemClock.GetField("SystemClockChanged", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(null);
        set.Should().NotBeNull("System.Reactive's SystemClock keeps its registrations in a static SystemClockChanged set");
        return set!;
    }

    private static int LiveSyncHubs(IMessageHub hub) =>
        hub.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .Hubs.Count(h => h.Address.Type == SynchronizationAddress.AddressType);

    /// <summary>
    /// Runs <paramref name="subject"/> while a dedicated thread HOLDS SystemClock's registration lock. The
    /// negative control (Rx's own <c>StartWith</c>, which must not complete under the hold) runs first, so a
    /// green subject cannot come from a hold that did not take effect. The hold is released in a
    /// <c>finally</c>; it is bounded at 90 s, well past every budget here, so a subject that needs the lock
    /// fails on its budget and never on the hold ending.
    /// </summary>
    private async Task UnderHeldSystemClockLock(Func<Task> subject)
    {
        var gate = SystemClockRegistrationLock();
        var holding = 0;
        var release = 0;
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                Volatile.Write(ref holding, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(90));
            }
        }) { IsBackground = true, Name = "rx-systemclock-holder" };
        holder.Start();
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref holding) == 1, Budget).Should().BeTrue("the holder took the lock");

            var controlDone = 0;
            new Thread(() =>
            {
                Observable.Return(1).StartWith(0).Subscribe(_ => { }, () => Volatile.Write(ref controlDone, 1));
            }) { IsBackground = true, Name = "rx-startwith-control" }.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref controlDone) == 1, TimeSpan.FromSeconds(1)).Should().BeFalse(
                "StartWith registers an AsyncLockScheduler under SystemClock's lock, so the hold must stop it — otherwise this test proves nothing");
            Output.WriteLine("holding SystemClock's lock; the StartWith control is blocked as expected");

            await subject();
            Output.WriteLine("the subject completed under the hold");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>A client with its data context up and two more live remote streams, each on its own hosted sync/{id} hub.</summary>
    private async Task<(IMessageHub Client, IWorkspace Workspace)> ClientWithLiveSyncHubs()
    {
        GetHost();
        var client = GetClient();
        var workspace = client.ServiceProvider.GetRequiredService<IWorkspace>();
        await workspace.GetObservable<BusinessUnit>().Should().Within(Budget)
            .Match(x => x.Count > 0, "the owner must serve the initial snapshot",
                cancellationToken: TestContext.Current.CancellationToken);

        var first = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(BusinessUnit)));
        var second = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            CreateHostAddress(), new CollectionReference(nameof(LineOfBusiness)));
        await first.Where(change => change.Value is { Instances.Count: > 0 }).Should().Within(Budget)
            .Emit("the first remote stream must be live", cancellationToken: TestContext.Current.CancellationToken);
        await second.Where(change => change.Value is { Instances.Count: > 0 }).Should().Within(Budget)
            .Emit("the second remote stream must be live", cancellationToken: TestContext.Current.CancellationToken);
        var syncHubs = LiveSyncHubs(client);
        Output.WriteLine($"sync hubs on the client: {syncHubs}");
        syncHubs.Should().BeGreaterThanOrEqualTo(2, "the hubs under test are hosted sync/* hubs, so they must exist");
        return (client, workspace);
    }

    /// <summary>
    /// The verdict's own subject: a hub carrying live remote streams is torn down — its hosted sync/* hubs in
    /// its <c>DisposeHostedHubs</c> phase, its workspace with them — while the lock is held.
    /// <c>Dispose()</c> runs on its own thread: it is synchronous and must return at once, and a call that
    /// blocked the test thread on the lock would otherwise only be measured after the hold had ended.
    /// </summary>
    [HubFact]
    public async Task HostedSyncHubs_TearDown_WhileRxsSystemClockLockIsHeld()
    {
        var (client, _) = await ClientWithLiveSyncHubs();
        await UnderHeldSystemClockLock(async () =>
        {
            var disposeReturned = 0;
            new Thread(() =>
            {
                client.Dispose();
                Volatile.Write(ref disposeReturned, 1);
            }) { IsBackground = true, Name = "client-dispose" }.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref disposeReturned) == 1, Budget).Should().BeTrue(
                "Dispose() is synchronous and returns at once; it must not wait for Rx's process-wide SystemClock lock");
            await client.DisposalCompleted.FirstOrDefaultAsync().Should().Within(Budget)
                .Emit("tearing down a hub's hosted sync/* hubs and its data layer must not need Rx's process-wide SystemClock lock",
                    cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    /// <summary>
    /// A write through a live remote stream — the client's <see cref="DataChangeRequest"/> applied by its
    /// workspace, carried by its sync/* hub to the owner, applied there — while the lock is held. These are
    /// the turns a sync/* hub takes all the time; a worker parked on the lock inside one of them is a worker no
    /// other hub's drain gets.
    /// </summary>
    [HubFact]
    public async Task AWriteThroughALiveRemoteStream_CompletesWhileRxsSystemClockLockIsHeld()
    {
        var (client, workspace) = await ClientWithLiveSyncHubs();
        var hostWorkspace = GetHost().ServiceProvider.GetRequiredService<IWorkspace>();
        var businessUnit = (await workspace.GetObservable<BusinessUnit>().Should().Within(Budget)
            .Emit(cancellationToken: TestContext.Current.CancellationToken)).First();
        var renamed = businessUnit with { DisplayName = businessUnit.DisplayName + " (renamed under the held lock)" };

        await UnderHeldSystemClockLock(async () =>
        {
            client.Post(new DataChangeRequest { Updates = [renamed] });
            await workspace.GetObservable<BusinessUnit>(renamed.SystemName).Should().Within(Budget)
                .Match(x => x is not null && x.DisplayName == renamed.DisplayName,
                    "the client's own workspace must apply the write without Rx's process-wide SystemClock lock",
                    cancellationToken: TestContext.Current.CancellationToken);
            await hostWorkspace.GetObservable<BusinessUnit>(renamed.SystemName).Should().Within(Budget)
                .Match(x => x is not null && x.DisplayName == renamed.DisplayName,
                    "the owner must receive and apply the write through the sync/* hub without Rx's process-wide SystemClock lock",
                    cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    /// <summary>
    /// The per-revision admission every followed read stream runs on its sync/* hub
    /// (<c>JsonSynchronizationStream.FollowReadAdmission</c>, <c>MeshNodeStreamCache</c>): one verdict per
    /// revision, so on a busy stream this runs per frame. The revisions are delivered from their own thread,
    /// so a delivery parked on the lock is measured against the budget rather than blocking the test.
    /// </summary>
    [HubFact]
    public async Task ReadAdmission_ForwardsRevisionsWhileRxsSystemClockLockIsHeld()
    {
        await UnderHeldSystemClockLock(async () =>
        {
            var revisions = new System.Reactive.Subjects.Subject<int>();
            var admitted = revisions.AdmitEachRevision(_ => Observable.Return(true), Budget,
                () => new UnauthorizedAccessException("refused")).Replay();
            var subscribed = 0;
            var delivered = 0;
            new Thread(() =>
            {
                using var connection = admitted.Connect();
                Volatile.Write(ref subscribed, 1);
                revisions.OnNext(1);
                revisions.OnNext(2);
                Volatile.Write(ref delivered, 1);
                SpinWait.SpinUntil(() => false, Budget);
            }) { IsBackground = true, Name = "revision-producer" }.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref subscribed) == 1, Budget).Should().BeTrue(
                "subscribing a read admission must not wait for Rx's process-wide SystemClock lock");
            SpinWait.SpinUntil(() => Volatile.Read(ref delivered) == 1, Budget).Should().BeTrue(
                "delivering revisions into a read admission must not wait for Rx's process-wide SystemClock lock");
            await admitted.Where(revision => revision == 2).Should().Within(Budget)
                .Emit("an admitted revision must be forwarded without Rx's process-wide SystemClock lock",
                    cancellationToken: TestContext.Current.CancellationToken);
        });
    }
}
