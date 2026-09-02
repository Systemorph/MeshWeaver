using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2938 — the pod-hub claim was a ONE-SHOT assertion, and the thing it asserts into is
/// re-partitioned on every membership change.</b>
///
/// <para><b>The defect.</b> <c>OrleansRoutingService.AttachPodHub</c> publishes an address→silo
/// mapping by activating the address's <c>IPodHubGrain</c> on the owning silo, so Orleans' own grain
/// directory becomes the map. That is the whole trick of the transport — and the roll plan's own
/// ledger names the price: <i>"the grain directory is ALSO the component that is unstable while
/// cluster membership changes"</i> (<c>Doc/Architecture/PodHubDeliveryRollPlan</c> → "What the swap
/// traded"). The claim stopped the instant <c>Attach</c> first answered <c>true</c>. So when the
/// mapping was lost — a directory range moving between silos as a pod joins, leaves or is
/// rescheduled — <b>nothing ever re-made it</b>, and nothing on this side could even notice: the
/// router that can no longer resolve the address NACKs the SENDER, never the owner.</para>
///
/// <para><b>What that costs, measured.</b> On <c>memex-cloud</c> (2026-09-01, Loki, 31 d retention):
/// a LIVE pod's <c>cache/{meshId}</c> hub refused from three other LIVE pods; a flat ~40 refusals an
/// hour for twelve hours against one pod's <c>portal/nodeops-{meshId}</c> and <c>cache/{meshId}</c>,
/// spanning a container restart; every pod logging the claim's budget-exhausted <c>Warning</c> at
/// startup; and — decisively — <b>zero</b> "landed after its initial budget was exhausted" lines in
/// eight days across 36 M log lines. A claim that missed never came back.</para>
///
/// <para><b>The rule.</b> Landing is not a terminal any more than a counter was (#2426). The claim's
/// lifetime is its REGISTRATION's, so it is re-asserted on every cluster membership change — the
/// event that can invalidate it, never a timer and never a poll. That is the same move Orleans' own
/// <c>ClientDirectory</c> makes when it re-publishes its client routing table to every silo on every
/// membership change, and for the same reason.</para>
///
/// <para><b>No cluster, no clock, no mocks of anything of ours.</b> The subject is the real
/// <see cref="OrleansRoutingService"/>; the grain factory is a RECORDER whose pod-hub grain answers
/// <c>true</c> — the exact answer <c>PodHubGrain.Attach</c> gives on the owning silo — and the
/// membership feed is a real <see cref="IClusterMembershipFeed"/> the test pushes, which is what the
/// silo's own <c>ISiloStatusListener</c> does with an Orleans notification.</para>
///
/// <para><b>Fails on unfixed code:</b>
/// <see cref="AClaimThatLanded_IsReAsserted_OnEveryMembershipChange"/> observes exactly one
/// <c>Attach</c> for the life of the process, however much the cluster moves underneath it.</para>
/// </summary>
public class PodHubClaimReassertionTest
{
    private static readonly Address Hub = new("portal", "reassert");

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static IObservable<IMessageDelivery> Ignore(IMessageDelivery d, CancellationToken _) =>
        Observable.Return(d);

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Services(
        IClusterMembershipFeed? feed)
    {
        var services = new ServiceCollection();
        // Registered but never fired: the stream attach then stays parked on the #1129 readiness
        // gate, so this test needs no stream provider and touches none.
        services.AddSingleton(new OrleansStreamingReadiness());
        if (feed is not null)
            services.AddSingleton(feed);
        return services.BuildServiceProvider();
    }

    private static OrleansRoutingService Router(
        AcceptingGrainFactory factory, IServiceProvider sp, RecordingLogger logger) =>
        new(factory, sp, logger)
        {
            // Instance seams, never static state — the POLICY is under test, not the clock.
            CanHostGrains = true,
            ClaimBackoff = _ => TimeSpan.FromMilliseconds(1),
        };

    /// <summary>
    /// Waits on the CONDITION itself — the grain publishes its running attach count, so this is a
    /// filter on a live sequence rather than a poll. The <see cref="Budget"/> is a backstop against
    /// a hang, never the measurement, and the failure it raises names what was expected.
    ///
    /// <para>🚨 <c>ObservableAwait.Await</c>, never <c>.ToTask()</c> and never a bare
    /// <c>await source</c>: Rx's own awaiter is an <c>AsyncSubject</c> that resumes the test INLINE
    /// on the signalling thread, still inside Rx's trampoline, and every later <c>await</c> in the
    /// method inherits that scheduler.</para>
    /// </summary>
    private static async Task<int> WaitForAttaches(AcceptingGrainFactory factory, int target, string because)
    {
        try
        {
            return await factory.Attaches
                .Where(count => count >= target)
                .Take(1)
                .Timeout(Budget)
                .Await();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"the claim was asserted {factory.AttachCalls} time(s), short of {target}: {because}");
        }
    }

    /// <summary>
    /// 🚨 THE PIN. A claim that HAS landed must be re-asserted when the cluster's membership moves,
    /// because that is exactly when the grain directory it published into is re-partitioned.
    /// </summary>
    [Fact]
    public async Task AClaimThatLanded_IsReAsserted_OnEveryMembershipChange()
    {
        var feed = new TestMembershipFeed();
        var factory = new AcceptingGrainFactory();
        await using var sp = Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1, "the initial claim must still be made immediately");

        feed.PushChange();
        await WaitForAttaches(factory, 2,
            "a membership change re-partitions Orleans' grain directory, which is where this claim's "
            + "address→silo mapping lives — a mapping lost there is lost silently on the owning side, "
            + "so the claim must be re-made rather than assumed to have survived (#2938)");

        feed.PushChange();
        feed.PushChange();
        await WaitForAttaches(factory, 4,
            "EVERY membership change, not just the first — the ClientDirectory prior art republishes "
            + "its whole routing table on each one");
    }

    /// <summary>
    /// The landing is still reported ONCE. Re-assertion must not turn a per-hub Debug line into a
    /// per-hub-per-membership-change line, and must not re-complete the settled seam.
    /// </summary>
    [Fact]
    public async Task ReAssertion_ReportsTheLandingOnce_AndSettlesOnce()
    {
        var feed = new TestMembershipFeed();
        var factory = new AcceptingGrainFactory();
        var logger = new RecordingLogger();
        await using var sp = Services(feed);
        using var routing = Router(factory, sp, logger);

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1, "the initial claim");

        var settled = routing.PodHubClaimSettled(Hub)
                      ?? throw new InvalidOperationException(
                          "the routing service recorded no pod-hub claim for the address — the seam "
                          + "this test waits on is gone");
        await settled.Timeout(Budget).LastOrDefaultAsync().Await();

        feed.PushChange();
        feed.PushChange();
        await WaitForAttaches(factory, 3, "the two membership changes");

        logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning,
            "nothing here is abnormal — the claim lands every time it is asked");
        logger.Records.Count(r => r.Message.Contains("landed on this process")).Should().Be(1,
            "the FIRST landing is the one worth a line; a re-assertion that logs the same sentence "
            + "again turns one line per hub into one line per hub per membership change, which is "
            + "the storm shape #2426/#2546 exist to remove");
    }

    /// <summary>
    /// TERMINAL, unchanged: the claim is alive exactly as long as the hub it claims for. A
    /// membership change after the registration is gone must not resurrect it — re-assertion is a
    /// property of a LIVE registration, not of the address.
    /// </summary>
    [Fact]
    public async Task DisposingTheRegistration_EndsReAssertion()
    {
        var feed = new TestMembershipFeed();
        var factory = new AcceptingGrainFactory();
        await using var sp = Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1, "the initial claim");
        feed.PushChange();
        await WaitForAttaches(factory, 2, "one re-assertion, so the feed is proven live before disposal");

        registration.Dispose();

        // Negative assertion with no positive signal to filter for: let anything already in flight
        // land, take the reading, push changes, and require the reading to be unchanged.
        await Task.Delay(250);
        var afterDisposal = factory.AttachCalls;
        feed.PushChange();
        feed.PushChange();
        await Task.Delay(400);
        factory.AttachCalls.Should().Be(afterDisposal,
            "a disposed registration has no local route left, so re-claiming its address would place "
            + "an activation for a hub that no longer exists — and the claim's lifetime is the "
            + "registration's, in both directions");
    }

    /// <summary>
    /// The other direction, and what keeps this from being a behaviour change everywhere: where no
    /// membership feed is registered — an Orleans CLIENT, a monolith, a bare mesh in a unit test —
    /// membership cannot change under this process, so the claim is asserted exactly once, which is
    /// byte-for-byte what it did before.
    /// </summary>
    [Fact]
    public async Task WithNoMembershipFeed_TheClaimIsAssertedExactlyOnce()
    {
        var factory = new AcceptingGrainFactory();
        await using var sp = Services(feed: null);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1, "the initial claim is made with or without a feed");

        await Task.Delay(400);
        factory.AttachCalls.Should().Be(1,
            "with nothing that can invalidate the claim there is nothing to re-assert, and a "
            + "re-assertion on no signal at all would be the poll this design refuses to become");
    }

    /// <summary>
    /// A membership feed a test drives directly — the same shape
    /// <c>OrleansClusterMembershipFeed</c> presents when Orleans' silo-status oracle notifies it.
    /// </summary>
    private sealed class TestMembershipFeed : IClusterMembershipFeed, IDisposable
    {
        private readonly Subject<long> changes = new();
        private long sequence;

        public IObservable<long> Changes => changes;

        public void PushChange() => changes.OnNext(Interlocked.Increment(ref sequence));

        public void Dispose()
        {
            changes.OnCompleted();
            changes.Dispose();
        }
    }

    /// <summary>
    /// The pod-hub grain a claim that CAN land meets: <c>Attach</c> answers <c>true</c>, verbatim
    /// what <c>PodHubGrain.Attach</c> returns on the silo that owns the address. Counting happens on
    /// the GRAIN, so a <c>Detach</c> during teardown can never be mistaken for another assertion.
    /// </summary>
    private sealed class AcceptingPodHubGrain : IPodHubGrain, IDisposable
    {
        // 🚨 A BehaviorSubject, so a waiter that subscribes AFTER the count already reached its
        // target still sees it. With a plain Subject the test would be a race it usually wins,
        // which is the worst kind of green.
        private readonly BehaviorSubject<int> attaches = new(0);
        private int attachCalls;
        public int AttachCalls => Volatile.Read(ref attachCalls);

        /// <summary>The running attach count — the CONDITION the tests wait on.</summary>
        public IObservable<int> Attaches => attaches;

        public Task<bool> Attach()
        {
            // Switch keeps exactly one claim in flight per address, so these are serialised.
            attaches.OnNext(Interlocked.Increment(ref attachCalls));
            return Task.FromResult(true);
        }

        public void Dispose()
        {
            attaches.OnCompleted();
            attaches.Dispose();
        }

        public Task Detach() => Task.CompletedTask;

        public Task<IMessageDelivery> Deliver(IMessageDelivery delivery) => Task.FromResult(delivery);
    }

    /// <summary>
    /// Hands out the one accepting pod-hub grain. Only the string-key overload is implemented
    /// because it is the only shape the mesh uses — every other member throws, so a new call shape
    /// fails loudly here instead of passing silently.
    /// </summary>
    private sealed class AcceptingGrainFactory : IGrainFactory
    {
        private readonly AcceptingPodHubGrain podHub = new();

        public int AttachCalls => podHub.AttachCalls;

        /// <summary>The running attach count — see <see cref="AcceptingPodHubGrain.Attaches"/>.</summary>
        public IObservable<int> Attaches => podHub.Attaches;

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IPodHubGrain))
                return (TGrainInterface)(object)podHub;
            throw new NotSupportedException($"Unexpected grain interface {typeof(TGrainInterface)}");
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }

    /// <summary>
    /// Captures what the router logged, at which level — the assertions here are over the LEVEL and
    /// the COUNT, so a null logger would make them vacuous.
    /// </summary>
    private sealed class RecordingLogger : ILogger<OrleansRoutingService>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> records = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Records => records.ToArray();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
