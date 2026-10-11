using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #6429 — a membership change that arrives WHILE a stream attach is in flight must
/// not be thrown away.</b>
///
/// <para>#3139 made a given-up attach repairable: the next cluster membership change re-attaches
/// it. The rule that guards it (<c>ShouldReattach</c>) declines an attach that has not finished,
/// and that is right. But the change is an EDGE. It was evaluated once, at the instant it arrived,
/// and "not finished" discarded it. An attach that gave up afterwards then had no change left to
/// repair it, and the hub stayed in <i>"cross-process routing DISABLED"</i> until some later,
/// unrelated change.</para>
///
/// <para>That order is the common one. What makes an attach spend its whole budget is a silo that
/// has stopped answering (six attempts at Orleans' 30 s response timeout), and the change that
/// removes that silo from the cluster lands while the budget is still running.</para>
///
/// <para>The feed here is one the test pushes, and the push happens from INSIDE the first attach
/// attempt, so "the change arrived while the attach was in flight" is a fact of the call stack and
/// not a race the test hopes to win. The attach fails with a non-transient fault, so each attach is
/// exactly one attempt and the attempt count IS the number of attaches. No cluster, no clock.</para>
///
/// <para><b>Fails on the unfixed code:</b>
/// <see cref="AChangeArrivingWhileTheAttachIsInFlight_ReattachesOnceItGivesUp"/> never sees a second
/// attempt.</para>
/// </summary>
public class StreamReattachHoldsAChangeMidAttachTest
{
    /// <summary>A backstop against a hang, never the measurement: with no backoff the real wait is milliseconds.</summary>
    private static TimeSpan Backstop => TestTimeouts.Convergence;

    /// <summary>How long "nothing further happens" is watched for. A negative has no positive signal.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);

    /// <summary>A membership feed the test pushes. Synchronous, so a push has been HANDLED when it returns.</summary>
    private sealed class PushedFeed : IClusterMembershipFeed, IDisposable
    {
        private readonly Subject<long> changes = new();
        private long sequence;

        public IObservable<long> Changes => changes;

        public void Push() => changes.OnNext(Interlocked.Increment(ref sequence));

        public void Dispose() => changes.Dispose();
    }

    /// <summary>
    /// Counts attach attempts and fails each one with a fault the classifier calls permanent.
    /// <paramref name="duringAttempt"/> runs inside the attempt, before it fails.
    /// </summary>
    private sealed class FailingStreamProvider(Action<int> duringAttempt) : IStreamProvider, IDisposable
    {
        private readonly ReplaySubject<int> attempts = new();
        private int count;

        /// <summary>Each attempt's 1-based number, replayed.</summary>
        public IObservable<int> Attempts => attempts;

        public string Name => StreamProviders.Memory;
        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            var n = Interlocked.Increment(ref count);
            duringAttempt(n);
            attempts.OnNext(n);
            throw new NotSupportedException("attach probe — a permanent failure, so one attempt per attach");
        }

        public void Dispose() => attempts.Dispose();
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider services;
        private readonly OrleansStreamingReadiness readiness = new();
        private readonly OrleansRoutingService routing;
        private readonly IDisposable registration;

        public Rig(Action<PushedFeed, int> duringAttempt)
        {
            Provider = new FailingStreamProvider(n => duringAttempt(Feed, n));
            var collection = new ServiceCollection();
            collection.AddSingleton(readiness);
            collection.AddSingleton<IClusterMembershipFeed>(Feed);
            collection.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, Provider);
            services = collection.BuildServiceProvider();

            // grainFactory is null on purpose, as in StreamAttachTransientRetryTest: this process is
            // not a silo, so nothing here may place a grain.
            routing = new OrleansRoutingService(null!, services, NullLogger<OrleansRoutingService>.Instance)
            {
                AttachBackoff = _ => TimeSpan.Zero
            };
            var address = AddressExtensions.CreateMeshAddress($"reattach-{Guid.NewGuid():N}");
            registration = routing.RegisterStream(address, (d, _) => Observable.Return(d));
            FirstAttachSettled = routing.AttachSettled(address)
                                 ?? throw new InvalidOperationException(
                                     "RegisterStream recorded no attach completion for the address.");
        }

        public PushedFeed Feed { get; } = new();
        public FailingStreamProvider Provider { get; }

        /// <summary>Completes when the FIRST attach has stopped attempting.</summary>
        public Task FirstAttachSettled { get; }

        /// <summary>Opens the readiness gate the way Orleans does: the lifecycle observer's OnStart.</summary>
        public Task OpenTheGate() => ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        public IObservable<int> Attempt(int number) => Provider.Attempts.Where(n => n == number);

        public async ValueTask DisposeAsync()
        {
            registration.Dispose();
            routing.Dispose();
            await services.DisposeAsync();
            Feed.Dispose();
            Provider.Dispose();
        }
    }

    /// <summary>
    /// 🚨 THE PIN. The change is pushed from inside the first attempt, so it is handled while the
    /// attach is in flight. The attach then gives up. A second attach must follow.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AChangeArrivingWhileTheAttachIsInFlight_ReattachesOnceItGivesUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig((feed, attempt) =>
        {
            if (attempt == 1)
                feed.Push();
        });

        await rig.OpenTheGate();

        await rig.Attempt(2).Should().Within(Backstop).Emit(
            "the cluster's membership moved while the first attach was still attempting, so its "
            + "give-up was a verdict on a cluster shape that no longer exists. Dropping that change "
            + "left the hub's cross-process routing disabled until an unrelated later change (#6429)",
            ct);
    }

    /// <summary>
    /// NEGATIVE CONTROL. The same failed attach with NO membership change is left alone. Without
    /// this the pin above would also pass on a timer, or on a retry that fires whatever happened.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AGiveUpWithNoChangeSinceTheAttachBegan_IsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig((_, _) => { });

        await rig.OpenTheGate();
        await rig.FirstAttachSettled.WaitAsync(Backstop, ct);

        await rig.Attempt(2).Should().NotEmit(Quiet,
            "nothing has happened that could make a second attempt end differently. A re-attach "
            + "here would be a timer or an unconditional retry, which is the shape this is not",
            ct);
    }

    /// <summary>
    /// The #3139 behaviour is kept: a change that arrives AFTER the give-up still re-attaches.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AChangeAfterTheGiveUp_StillReattaches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig((_, _) => { });

        await rig.OpenTheGate();
        await rig.FirstAttachSettled.WaitAsync(Backstop, ct);
        rig.Feed.Push();

        await rig.Attempt(2).Should().Within(Backstop).Emit(
            "a given-up attach is repaired by the next membership change (#3139)", ct);
    }

    /// <summary>
    /// A roll delivers several changes in a row. Held behind ONE attach they are one reason to try
    /// again, not three: against a target that is still down, each extra attach would spend a full
    /// budget and write its own Critical.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ABurstOfChangesDuringOneAttach_ReattachesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = new Rig((feed, attempt) =>
        {
            if (attempt != 1)
                return;
            feed.Push();
            feed.Push();
            feed.Push();
        });

        await rig.OpenTheGate();

        await rig.Attempt(2).Should().Within(Backstop).Emit(
            "the burst arrived while the first attach was in flight, so it is repaired", ct);
        await rig.Attempt(3).Should().NotEmit(Quiet,
            "three changes held behind one attach describe one moved cluster. Only the newest acts; "
            + "the second attach began after all three, so none of them is a reason for a third",
            ct);
    }
}
