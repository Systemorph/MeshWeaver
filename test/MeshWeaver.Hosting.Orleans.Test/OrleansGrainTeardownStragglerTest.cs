using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error ?? "");
        var threadPath = createResp.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Activate the grain by routing a read to it (grain activation → hub build →
        //    CompleteActivation stamps the lifetime callbacks on the hub configuration).
        var getResp = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(threadPath)))
            .FirstAsync().ToTask(ct);
        getResp.Message.Data.Should().NotBeNull("the grain must have activated and served its node");

        // 3. Reach the silo-side grain-hosted hub and capture the callbacks the grain
        //    hands out to hub code (heartbeats → KeepAlive, rounds → BeginOperation,
        //    stuck-round watchdog → Invoke). These are exactly what stragglers call.
        var hub = FindSiloHostedHub(threadPath);
        hub.Should().NotBeNull($"the grain-hosted hub for {threadPath} must exist on the silo");
        var keepAlive = hub!.Configuration.Get<GrainKeepAliveCallback>();
        var longRunning = hub.Configuration.Get<GrainLongRunningOperationCallback>();
        var deactivate = hub.Configuration.Get<GrainDeactivateCallback>();
        keepAlive.Should().NotBeNull();
        longRunning.Should().NotBeNull();
        deactivate.Should().NotBeNull();

        // 4. Deactivate while ALIVE (legal — the #147 escape hatch) and wait for the
        //    activation to be FULLY gone: first the hub's own disposal completes
        //    (OnDeactivateAsync), then the activation disappears from the silo catalog,
        //    which happens only after ActivationData.FinishDeactivating set
        //    State=Invalid. No sleeps — both waits are on the actual condition.
        deactivate!.Invoke();
        await hub.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .ToTask(ct);
        Output.WriteLine("Hub disposal completed — waiting for the activation to leave the catalog...");

        var grainId = $"messagehub/{threadPath}";
        var mgmt = Fixture.Cluster.Client.GetGrain<IManagementGrain>(0);
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .StartWith(0L)
            .SelectMany(_ => mgmt.GetDetailedGrainStatistics().ToObservable())
            .Where(stats => stats.All(s => !string.Equals(s.GrainId.ToString(), grainId, StringComparison.OrdinalIgnoreCase)))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .ToTask(ct);
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
