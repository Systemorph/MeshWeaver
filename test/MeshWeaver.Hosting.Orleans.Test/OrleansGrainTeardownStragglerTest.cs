using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Deterministic repro for the ORLEANS TEARDOWN RACE (CI run 28646145008 shard 2):
/// during test-class teardown a straggler (a change-feed / activation-source emission,
/// a heartbeat, a round start, a disposal action) still reaches the per-node
/// <see cref="MessageHubGrain"/> AFTER its activation completed deactivation
/// (<c>State=Invalid</c>). The grain-lifetime calls those stragglers make —
/// <c>Grain.DelayDeactivation</c> (via <c>GrainKeepAliveCallback</c> /
/// <c>GrainLongRunningOperationCallback</c>) and <c>Grain.DeactivateOnIdle</c> (via
/// <c>GrainDeactivateCallback</c> / <c>RegisterForDisposal</c> / the activation-source
/// terminal handlers) — then hit Orleans'
/// <c>GrainRuntime.CheckRuntimeContext</c>, which THROWS
/// <c>InvalidOperationException("Attempt to access an invalid activation: …")</c> instead
/// of no-opping. On the real teardown path that throw escapes RAW into the activation
/// source's Rx chain (proven stack: <c>MessageHubGrain.CompleteActivation</c> →
/// <c>DeactivateOnIdle</c> inside its own catch block → the path-resolver
/// <c>MeshQuery</c> emission → <c>TaskPoolScheduler.ScheduledWorkItem</c>), faults a
/// ThreadPool task nobody observes, and xUnit v3 escalates the
/// <c>UnobservedTaskException</c> to a Catastrophic failure that poisons the NEXT test
/// class (OrleansSubThreadRoutingTest died as collateral in the incident).
///
/// <para>This test distills the race deterministically: activate a per-thread grain,
/// capture the grain-lifetime callbacks it hands to hub code, drive the activation to
/// FULL deactivation (gone from the silo catalog ⇒ <c>State=Invalid</c>), then invoke the
/// callbacks the way a straggler would. Contract under test: a dead activation is a
/// GRACEFUL TERMINAL for grain-lifetime calls — "deactivate" is already achieved and
/// "keep alive" is moot — so the callbacks must log-and-no-op, never throw. Pre-fix this
/// fails with the exact incident exception.</para>
///
/// <para>🚨 <b>HOW IT WAITS IS PART OF WHAT IT TESTS — it was DEADLOCKING (issue #2301, four
/// recurrences, the fourth on <c>main</c> with a <c>HOST_CRASHED</c> marker).</b> The setup above
/// used to be reached by BRIDGING every wait to a Task: the request/response reads and
/// <c>DisposalCompleted</c> through <c>FirstAsync()/FirstOrDefaultAsync().ToTask()</c>, and "the
/// activation left the catalog" through a 100 ms POLL of
/// <c>IManagementGrain.GetDetailedGrainStatistics()</c> raced against a 30 s <c>Timeout</c>.</para>
///
/// <para>Rx's <c>ToTask()</c> completes its <c>TaskCompletionSource</c> from inside the pipeline
/// WITHOUT <c>RunContinuationsAsynchronously</c>, so the <c>await</c> resumed this test method
/// INLINE on whichever thread signalled — and for <c>DisposalCompleted</c> that thread is the hub's
/// disposal thread, which runs on THE DEACTIVATING GRAIN'S OWN TURN SCHEDULER
/// (<c>CompleteActivation</c> builds the hub <c>.WithTaskScheduler(grainScheduler)</c>). The rest
/// of the method then ran there, holding that scheduler for up to 30 s while waiting for that same
/// grain's activation to leave the catalog — which needs
/// <c>ActivationData.FinishDeactivating</c> to make progress on the scheduler being held. The
/// failure was <b>always exactly 30 s</b>, the <c>Timeout</c> budget, never a distribution around
/// it; a healthy activation leaves the catalog in 0.10 s. That shape is a deadlock, not
/// contention.</para>
///
/// <para>The unobserved fault is the second-order effect that turned it into a crash: when the
/// <c>Timeout</c> settled the wait, a fresh batch of catalog queries was still in flight against a
/// silo mid-teardown, and each could fault into nothing, become an <c>UnobservedTaskException</c>,
/// and be escalated by xUnit v3 into the very Catastrophic failure this docstring describes above.
/// The test that exists to prove stragglers are handled gracefully was itself producing an
/// unobservable straggler.</para>
///
/// <para><b>Every wait in this method is now
/// <see cref="ReactiveCompletion.ObserveCompletion{T}"/></b> — a <c>Subscribe</c> whose task
/// completes with <c>RunContinuationsAsynchronously</c>, so no continuation can land on a hub or
/// grain scheduler. 🚨 <i>All</i> of them, including the two request/response reads, which the
/// <c>/async</c> skill would otherwise permit as <c>.ToTask()</c> in a test: with no
/// <c>SynchronizationContext</c>, <c>await</c> captures <c>TaskScheduler.Current</c>, so a SINGLE
/// inline resumption earlier in the method routes every LATER await onto that scheduler too.
/// Fixing only the last wait would leave the trap armed at the first one.</para>
///
/// <para>The poll is gone entirely, because the thing it was approximating now exists as a signal:
/// <see cref="GrainDeactivationCompleted"/>, published by the grain from Orleans' own
/// <c>IGrainContext.Deactivated</c>. Nothing here races a timeout against the work it is waiting
/// for; the wall-clock bound belongs to <c>[Fact(Timeout = …)]</c> and the cancellation token,
/// which is where a bound cannot orphan anything.</para>
/// </summary>
public class OrleansGrainTeardownStragglerTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    [Fact(Timeout = 60000)]
    public async Task GrainLifetimeCallbacks_AfterActivationIsInvalid_AreGracefulNoOps_NotThrows()
    {
        var ct = new CancellationTokenSource(55.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var client = GetClient($"straggler-{suffix}");

        // 1. Create a thread node — the incident's grain shape (every fataled grain was
        //    a TestUser/_Thread/... MessageHubGrain).
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", $"Teardown straggler {suffix}", "TestUser");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode), o => o.WithTarget(new Address("TestUser")))
            .ObserveCompletion(ex => Output.WriteLine($"[LATE FAULT] CreateNodeRequest stream faulted after answering: {ex}"), ct);
        createResp.Should().NotBeNull("the create request must be ANSWERED, not merely completed");
        createResp!.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Activate the grain by routing a read to it (grain activation → hub build →
        //    CompleteActivation stamps the lifetime callbacks on the hub configuration).
        var getResp = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(threadPath)))
            .ObserveCompletion(ex => Output.WriteLine($"[LATE FAULT] GetDataRequest stream faulted after answering: {ex}"), ct);
        getResp.Should().NotBeNull("the read must be ANSWERED, not merely completed");
        getResp!.Message.Data.Should().NotBeNull("the grain must have activated and served its node");

        // 3. Reach the silo-side grain-hosted hub and capture the callbacks the grain
        //    hands out to hub code (heartbeats → KeepAlive, rounds → BeginOperation,
        //    stuck-round watchdog → Invoke). These are exactly what stragglers call.
        //    Alongside them, the grain's own completion signal — what the activation TELLS
        //    a waiter, as opposed to what a straggler DOES to it.
        var hub = FindSiloHostedHub(threadPath);
        hub.Should().NotBeNull($"the grain-hosted hub for {threadPath} must exist on the silo");
        var keepAlive = hub!.Configuration.Get<GrainKeepAliveCallback>();
        var longRunning = hub.Configuration.Get<GrainLongRunningOperationCallback>();
        var deactivate = hub.Configuration.Get<GrainDeactivateCallback>();
        var deactivationCompleted = hub.Configuration.Get<GrainDeactivationCompleted>();
        keepAlive.Should().NotBeNull();
        longRunning.Should().NotBeNull();
        deactivate.Should().NotBeNull();
        deactivationCompleted.Should().NotBeNull(
            "a grain-hosted hub must publish its activation's deactivation-completed signal — " +
            "without it every waiter is back to polling the silo catalog (#2301)");

        // 4. Deactivate while ALIVE (legal — the #147 escape hatch) and wait for the
        //    activation to be FULLY gone: first the hub's own disposal completes
        //    (OnDeactivateAsync), then the activation itself reports that it is gone.
        //
        //    No sleeps, no polls, no timeouts. Both waits SUBSCRIBE, and their tasks complete
        //    with RunContinuationsAsynchronously — so this method is NEVER resumed on the
        //    disposing hub's thread, which is the deactivating grain's own turn scheduler. That
        //    is the #2301 deadlock: the old .ToTask() bridge resumed here inline on that
        //    scheduler and then held it for 30 s waiting for the deactivation that needed it.
        //    The error arm is the second half: a fault arriving after the wait settled is
        //    REPORTED (into the test output below) instead of orphaned into an unobserved task.
        deactivate!.Invoke();
        await hub.DisposalCompleted.ObserveCompletion(
            ex => Output.WriteLine($"[LATE FAULT] hub disposal for {threadPath} faulted AFTER it completed: {ex}"),
            ct);
        Output.WriteLine("Hub disposal completed — waiting for the activation to leave the catalog...");

        await deactivationCompleted!.Deactivated.ObserveCompletion(
            ex => Output.WriteLine($"[LATE FAULT] deactivation of {threadPath} faulted AFTER it completed: {ex}"),
            ct);
        Output.WriteLine("Activation reports itself fully deactivated. Confirming it left the catalog...");

        // The signal's CONTRACT, asserted rather than assumed. Orleans 10.2.2 completes
        // IGrainContext.Deactivated on the LAST line of ActivationData.FinishDeactivating —
        // after UnregisterMessageTarget() removed the activation from the silo catalog and after
        // DisposeAsync() set State=Invalid. This is ONE awaited call that answers a question, not
        // a loop that waits for an answer to change: the wait already happened, reactively, above.
        var grainId = $"messagehub/{threadPath}";
        var mgmt = Fixture.ClusterClient.GetGrain<IManagementGrain>(0);
        var catalog = await mgmt.GetDetailedGrainStatistics();
        catalog.Should().NotContain(
            s => string.Equals(s.GrainId.ToString(), grainId, StringComparison.OrdinalIgnoreCase),
            "GrainDeactivationCompleted fires from IGrainContext.Deactivated, which Orleans sets " +
            "AFTER UnregisterMessageTarget() — if this ever fails, that ordering changed and every " +
            "waiter built on the signal needs re-reading, not a poll bolted back on");
        Output.WriteLine("Activation is gone from the catalog (State=Invalid). Now the stragglers fire.");

        // 5. THE RACE, DISTILLED. Pre-fix each of these throws
        //    InvalidOperationException("Attempt to access an invalid activation: …") —
        //    the exact exception that escaped as the unobserved FATAL in CI. The
        //    contract: a dead activation is a graceful terminal — log-and-no-op.
        Record.Exception(() => keepAlive!.KeepAlive())
            .Should().BeNull("a heartbeat keep-alive after the activation died is moot — graceful no-op, never a throw");
        Record.Exception(() => longRunning!.BeginOperation().Dispose())
            .Should().BeNull("a round starting against a dead activation must not blow up the pooled task with an unobservable throw");
        Record.Exception(() => deactivate.Invoke())
            .Should().BeNull("requesting deactivation of an already-dead activation is the requested outcome — graceful no-op");
    }

    /// <summary>
    /// Finds the grain-hosted hub for <paramref name="path"/> on the silo mesh hub
    /// (test-only reflection, same approach as
    /// <see cref="SharedOrleansFixture.CleanupSiloHubsWithPrefix"/>).
    /// </summary>
    private IMessageHub? FindSiloHostedHub(string path)
    {
        foreach (var siloHandle in Fixture.Cluster.Silos)
        {
            var siloHost = siloHandle.GetType().GetProperty("SiloHost")?.GetValue(siloHandle) as IHost;
            var meshHub = siloHost?.Services.GetService(typeof(IMessageHub)) as IMessageHub;
            if (meshHub is null) continue;

            var field = meshHub.GetType().GetField("hostedHubs", BindingFlags.Instance | BindingFlags.NonPublic);
            var hosted = field?.GetValue(meshHub) as HostedHubsCollection;
            if (hosted is null) continue;

            var hub = hosted.Hubs.FirstOrDefault(h => h.Address.ToString() == path);
            if (hub is not null)
                return hub;
        }
        return null;
    }
}
