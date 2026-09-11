using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
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

    /// <summary>
    /// The backstop EVERY wait in this file is bounded by. A hang bound, never the measurement:
    /// with the claim behaving, each of these tests settles in milliseconds.
    ///
    /// <para>🚨 <b>It must stay strictly BELOW the runner's <c>methodTimeout</c></b> — 30 s, in this
    /// project's own <c>xunit.runner.json</c> — and that is why it is not a
    /// <see cref="TestTimeouts"/> value. Those scale by the CI factor (<c>Convergence</c> reaches
    /// 108 s on a runner, <c>Quick</c> 36 s), so on CI xunit would kill the test FIRST and report an
    /// anonymous timeout in place of the assertion naming what did not converge — the precise
    /// inversion <see cref="TestTimeouts"/> itself exists to prevent. Nothing here waits on a
    /// convergence whose cost scales with the machine: there is no cluster, no IO and no clock in
    /// these tests, only an in-process claim that either fires or does not.</para>
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a "no further claim arrives" reading watches for. Negative assertions are the one
    /// place a fixed wait is correct — there is no positive signal to filter for — so it is kept
    /// short, and derived from <see cref="Budget"/> rather than written as a second literal.
    /// </summary>
    private static TimeSpan StormWindow => Budget / 10;

    /// <summary>Attach-call ordinals no run reaches: the grain's default, unmodified behaviour.</summary>
    private const int NeverPark = -1;

    private const int NeverRefuse = -1;

    private static IObservable<IMessageDelivery> Ignore(IMessageDelivery d, CancellationToken _) =>
        Observable.Return(d);

    private static async Task<Microsoft.Extensions.DependencyInjection.ServiceProvider> Services(
        IClusterMembershipFeed? feed)
    {
        var readiness = new OrleansStreamingReadiness();
        var services = new ServiceCollection();
        services.AddSingleton(readiness);
        if (feed is not null)
            services.AddSingleton(feed);
        var provider = services.BuildServiceProvider();
        // The re-assertion policy begins only after the initial Active-stage readiness event. The
        // readiness test owns the pre-Active direction; these tests own what membership changes do
        // to a live claim afterwards.
        await ((ILifecycleObserver)readiness).OnStart(TestContext.Current.CancellationToken);
        return provider;
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
        await using var sp = await Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1,
            "the initial claim must be made as soon as the already-open readiness gate permits it");

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
    /// 🚨 THE PIN, half one: a membership change that arrives while the INITIAL claim is still
    /// inside its attach window must still be asserted.
    ///
    /// <para><b>What it caught.</b> <c>ClaimTriggers</c> composed the initial assertion as
    /// <c>Changes.StartWith(0L)</c>, and <c>StartWith</c> is a <c>Concat</c>: the feed is subscribed
    /// only once the prefix has been fully PROCESSED, and processing it runs the whole first round —
    /// <c>Attach</c> included — on the subscribing thread. <see cref="IClusterMembershipFeed.Changes"/>
    /// is hot and does not replay, so a change landing in that window was dropped WHERE IT WAS
    /// PUBLISHED: <c>Subject.OnNext</c> with no observer is a no-op. The claim then stood against a
    /// directory partitioning that had already moved, with nothing left to re-make it until the next
    /// change — the #2938 state that #3034 was supposed to have removed.</para>
    ///
    /// <para><b>Why this is deterministic and not a race the test usually wins.</b> The grain PARKS
    /// inside the first <c>Attach</c>, so the push below is made while the initial round is provably
    /// still in its window. On the unfixed composition the feed cannot yet be subscribed at that
    /// instant — <c>Concat</c> has not reached it — so the change is dropped every time, not
    /// sometimes.</para>
    ///
    /// <para><b>Why the count discriminates a MISSED assertion from an unobserved one.</b> The
    /// number this waits on is published by the GRAIN, from inside <c>Attach</c> itself. A claim
    /// that was made but whose result went unobserved still moves it; only a claim that was never
    /// made leaves it where it was.</para>
    /// </summary>
    [Fact]
    public async Task AMembershipChangeDuringTheInitialClaim_IsStillAsserted()
    {
        using var feed = new TestMembershipFeed();
        var factory = new AcceptingGrainFactory(parkOnCall: 1);
        await using var sp = await Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        try
        {
            SpinWait.SpinUntil(() => factory.IsParked, Budget).Should().BeTrue(
                "the initial claim must reach its attach window before this test can contend with "
                + "it — without that the contention is a coin toss and the reading below measures "
                + "nothing");

            // The contended push: a real membership change while the initial claim is demonstrably
            // mid-attach — the interleaving the shard-0 failures hit. It does not block, because the
            // feed hands its subscriber the same Scheduler.Default hop production's does.
            feed.PushChange();
        }
        finally
        {
            // Release into a worker the test deliberately parked, from a finally so a failing
            // assertion above can never strand the claim thread.
            factory.Release();
        }

        await WaitForAttaches(factory, 2,
            "a membership change that lands while the INITIAL claim is still in its attach window is "
            + "the one the grain directory most needs re-asserted against, and on the unfixed "
            + "composition the claim's own feed was not yet subscribed to hear it (#3931)");
    }

    /// <summary>
    /// 🚨 THE PIN, half two: the same obligation for a claim that is RETRYING — a DIFFERENT defect
    /// with the same symptom, which is why it needs its own reading.
    ///
    /// <para>Here the feed is demonstrably subscribed: the first attempt has already bounced, and
    /// the trigger sequence's prefix completed when it did. So the change reaches the claim. It was
    /// then thrown away one layer further in, by the claim/dispose handshake: a round that found
    /// <c>claimState == StartingAttempt</c> answered <c>Observable.Empty</c> — a TERMINAL answer to
    /// a TRANSIENT condition. Worse, <c>Switch</c> had already cancelled the round it found in
    /// progress, so the membership change DESTROYED an in-flight claim and made none of its own.</para>
    ///
    /// <para>A retry round re-subscribes on its backoff timer's thread while a membership round
    /// arrives on the feed's, so the overlap is ordinary — and it is at its most likely precisely
    /// during churn, because churn is what makes a claim bounce in the first place.</para>
    /// </summary>
    [Fact]
    public async Task AMembershipChangeDuringARetryingClaim_IsStillAsserted()
    {
        using var feed = new TestMembershipFeed();
        // Refuse the first attempt so the round retries, and park on the retry: by then the initial
        // trigger has been processed and the feed is subscribed, so this isolates the handshake.
        var factory = new AcceptingGrainFactory(parkOnCall: 2, refuseCall: 1);
        await using var sp = await Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        try
        {
            SpinWait.SpinUntil(() => factory.IsParked, Budget).Should().BeTrue(
                "the RETRY must reach its attach window before this test can contend with it — the "
                + "park is what makes the overlap certain rather than occasional");
            feed.PushChange();
        }
        finally
        {
            factory.Release();
        }

        await WaitForAttaches(factory, 3,
            "a membership change that lands while a RETRYING round is in its attach window carries "
            + "cluster shape that round predates, so it must make its own claim rather than be "
            + "discarded as 'someone is already starting one' — and Switch has already cancelled "
            + "the round it overlapped, so discarding it leaves NO claim at all (#3931)");
    }

    /// <summary>
    /// 🚨 THE OPPOSITE DIRECTION, and the reason the pins above cannot be satisfied by simply
    /// firing more often: ONE membership change is ONE claim, contended or not.
    ///
    /// <para>A fix that re-triggered the round — on the retry, on the trigger it could not place,
    /// on anything — would satisfy both pins above and reintroduce the claim storm
    /// <c>Switch</c> exists to bound: every attempt makes the owning silo log a line, so an
    /// over-asserting claim is the #2426/#2546 log-storm shape wearing a repair's colours.</para>
    /// </summary>
    [Fact]
    public async Task AContendedMembershipChange_IsAssertedExactlyOnce()
    {
        using var feed = new TestMembershipFeed();
        var factory = new AcceptingGrainFactory(parkOnCall: 1);
        await using var sp = await Services(feed);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        try
        {
            SpinWait.SpinUntil(() => factory.IsParked, Budget).Should().BeTrue(
                "the initial claim must reach its attach window before this test can contend with "
                + "it — this is the SAME interleaving as the positive pin, read the other way round");
            feed.PushChange();
        }
        finally
        {
            factory.Release();
        }

        await WaitForAttaches(factory, 2, "the contended change is asserted");

        // Negative, with no positive signal to filter for: a THIRD claim is the storm, and the only
        // honest instrument is to wait a bounded while and require it never to arrive.
        await factory.Attaches.Where(count => count >= 3).Should().NotEmit(StormWindow,
            "one membership change is one claim: the initial assertion plus this change is exactly "
            + "two, and a third would mean the contended round was re-fired rather than made once — "
            + "which is the claim storm Switch bounds and the log storm #2426/#2546 removed");
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
        await using var sp = await Services(feed);
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
        await using var sp = await Services(feed);
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
        await using var sp = await Services(feed: null);
        using var routing = Router(factory, sp, new RecordingLogger());

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttaches(factory, 1,
            "after readiness, the initial claim is made with or without a feed");

        await Task.Delay(400);
        factory.AttachCalls.Should().Be(1,
            "with nothing that can invalidate the claim there is nothing to re-assert, and a "
            + "re-assertion on no signal at all would be the poll this design refuses to become");
    }

    /// <summary>
    /// A membership feed a test drives directly — the same shape
    /// <c>OrleansClusterMembershipFeed</c> presents when Orleans' silo-status oracle notifies it.
    ///
    /// <para>🚨 <b>The <c>ObserveOn</c> is part of that shape, not decoration.</b> Production hands
    /// every subscriber <c>changes.ObserveOn(Scheduler.Default)</c> precisely so a subscriber's work
    /// — here, a grain call per registered address — can never delay Orleans' own membership
    /// processing. A test feed without it publishes INLINE on the pushing thread, so
    /// <see cref="PushChange"/> would block at the trigger sequence's merge gate whenever a claim
    /// round is in progress. That is the interleaving these tests deliberately create, so a feed
    /// that lacked the hop could not push into it at all.</para>
    /// </summary>
    private sealed class TestMembershipFeed : IClusterMembershipFeed, IDisposable
    {
        private readonly Subject<long> changes = new();
        private long sequence;

        public IObservable<long> Changes => changes.ObserveOn(Scheduler.Default);

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
    private sealed class AcceptingPodHubGrain(int parkOnCall = NeverPark, int refuseCall = NeverRefuse)
        : IPodHubGrain, IDisposable
    {
        // 🚨 A BehaviorSubject, so a waiter that subscribes AFTER the count already reached its
        // target still sees it. With a plain Subject the test would be a race it usually wins,
        // which is the worst kind of green.
        private readonly BehaviorSubject<int> attaches = new(0);
        private int attachCalls;
        private int parked;
        private int released;
        public int AttachCalls => Volatile.Read(ref attachCalls);

        /// <summary>The running attach count — the CONDITION the tests wait on.</summary>
        public IObservable<int> Attaches => attaches;

        /// <summary>True once the nominated <c>Attach</c> call is parked inside the grain.</summary>
        public bool IsParked => Volatile.Read(ref parked) == 1;

        /// <summary>
        /// Lets the parked <c>Attach</c> return. A plain volatile write into a worker the test
        /// deliberately parked — never a gate, and always called from a <c>finally</c> so a failing
        /// assertion cannot strand the claim thread.
        /// </summary>
        public void Release() => Volatile.Write(ref released, 1);

        public Task<bool> Attach()
        {
            var call = Interlocked.Increment(ref attachCalls);
            attaches.OnNext(call);
            // "Landed on a silo that is not the owner" — the answer that makes the round RETRY, so
            // a test can reach the retry window without a clock.
            if (call == refuseCall)
                return Task.FromResult(false);
            if (call == parkOnCall)
            {
                Volatile.Write(ref parked, 1);
                // Bounded, so a defect cannot hang the suite; the release is the real terminal.
                SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, Budget);
            }
            return Task.FromResult(true);
        }

        public void Dispose()
        {
            Release();
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
    private sealed class AcceptingGrainFactory(int parkOnCall = NeverPark, int refuseCall = NeverRefuse)
        : IGrainFactory
    {
        private readonly AcceptingPodHubGrain podHub = new(parkOnCall, refuseCall);

        public int AttachCalls => podHub.AttachCalls;

        /// <inheritdoc cref="AcceptingPodHubGrain.IsParked"/>
        public bool IsParked => podHub.IsParked;

        /// <inheritdoc cref="AcceptingPodHubGrain.Release"/>
        public void Release() => podHub.Release();

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
