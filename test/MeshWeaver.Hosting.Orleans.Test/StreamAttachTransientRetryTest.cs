using System;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2633 — a TRANSIENT stream-attach failure must not permanently disable a hub's
/// cross-process routing.</b>
///
/// <para><c>OrleansRoutingService.SubscribeWhenStreamingReadyAsync</c> attaches the hub's Orleans
/// memory-stream subscription once the #1129 readiness gate opens. Everything after that gate is a
/// CLUSTER call — <c>SubscribeAsync</c> resolves the stream's <c>PubSubRendezvousGrain</c> through
/// Orleans' grain directory — and the grain directory is precisely what is unstable while cluster
/// membership changes. Every rolling deploy produces that window, and Orleans' own
/// <c>ConnectionManager</c> says in the very message it fails with that it will reconnect within
/// ~0.5–0.9 s.</para>
///
/// <para><b>The defect.</b> The attach was a SINGLE attempt inside a catch-all, so one such
/// rejection latched the hub into <i>"cross-process routing for this hub is DISABLED"</i> for the
/// rest of its life — nothing re-attempted, and the loss persisted until the hub re-registered (a
/// circuit reconnect or a pod restart). Six per-user losses across three ReplicaSet generations on
/// memex-cloud, every one of them an <see cref="OrleansMessageRejectionException"/> that the
/// DELIVERY leg already classifies transient and retries
/// (<c>RoutingGrain.DeliverToGrainWithRetry</c>). The inconsistency between the two legs was the
/// whole defect.</para>
///
/// <para><b>What these tests pin, in both directions.</b> A transient attach failure is
/// re-attempted up to <c>SubscribeAttachRetries</c> times; a NON-transient one still gives up on
/// the first attempt (a permanent failure must still fail). Reaching the stream provider IS the
/// observable, so no cluster and no <c>IAsyncStream</c> fake is needed — and the backoff is
/// collapsed to zero through the instance seam, so neither case waits on a wall clock.</para>
///
/// <para><b>Fails on unfixed code:</b> <see cref="TransientAttachFailure_IsRetried_UpToTheBudget"/>
/// observes exactly ONE attempt instead of six.</para>
/// </summary>
public class StreamAttachTransientRetryTest
{
    /// <summary>
    /// Reaching <see cref="GetStream{T}"/> is the assertion: it counts attach attempts and throws
    /// the exception the test is about. The throw keeps the fake minimal — the attach-SUCCESS path
    /// is covered by the real-cluster routing tests.
    /// </summary>
    private sealed class ThrowingStreamProvider(Func<Exception> failure) : IStreamProvider
    {
        private int getStreamCalls;
        public int GetStreamCalls => Volatile.Read(ref getStreamCalls);
        public string Name => StreamProviders.Memory;
        public bool IsRewindable => false;

        public IAsyncStream<T> GetStream<T>(StreamId streamId)
        {
            Interlocked.Increment(ref getStreamCalls);
            throw failure();
        }
    }

    /// <summary>
    /// The production shape, in the real class: Orleans refuses to address the subscribe because
    /// its grain directory is mid-handoff, and the message is quoted verbatim from the #2633
    /// incident. <c>OrleansRoutingService.IsTransientFailure</c> already matches this TYPE — it
    /// simply was not consulted on this leg.
    ///
    /// <para>Constructed reflectively because Orleans keeps this exception's constructors
    /// <c>internal</c>: a hand-rolled stand-in would test the classifier against a type production
    /// never produces. <see cref="TheFakeIsTheProductionException"/> is the guard that keeps this
    /// honest — if an Orleans upgrade moves the constructor, that test fails by name instead of this
    /// one silently degrading.</para>
    /// </summary>
    private static Exception DirectoryRejection() =>
        (Exception)Activator.CreateInstance(
            typeof(OrleansMessageRejectionException),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                "Exception while sending message: Orleans.Runtime.Messaging.ConnectionFailedException: "
                + "Unable to connect to S10.244.4.150:11111:146475119, will retry after 582.6889ms"
            ],
            culture: null)!;

    /// <summary>
    /// Guard for the reflection above: the exception the retry tests are driven with must BE the
    /// production type AND must be the thing the classifier calls transient. Without this, a
    /// constructor that moved would leave the retry tests passing against nothing.
    /// </summary>
    [Fact]
    public void TheFakeIsTheProductionException()
    {
        var ex = DirectoryRejection();

        ex.Should().BeOfType<OrleansMessageRejectionException>(
            "the retry is gated on IsTransientFailure, which matches this exception by TYPE");
        OrleansRoutingService.IsTransientFailure(ex).Should().BeTrue(
            "if this ever goes false the attach retry below is inert and the tests prove nothing");
    }

    private static async Task<int> AttachAttemptsAsync(Func<Exception> failure)
    {
        var provider = new ThrowingStreamProvider(failure);
        var readiness = new OrleansStreamingReadiness();
        var services = new ServiceCollection();
        services.AddSingleton(readiness);
        services.AddKeyedSingleton<IStreamProvider>(StreamProviders.Memory, provider);
        await using var sp = services.BuildServiceProvider();

        // grainFactory is deliberately null — RegisterStream never places grains, so reaching one
        // would throw and fail the test (the same probe technique as the readiness-gate test).
        using var routing = new OrleansRoutingService(
            null!, sp, NullLogger<OrleansRoutingService>.Instance)
        {
            // Instance seam, not static state: the POLICY is what is under test, not the clock.
            AttachBackoff = _ => TimeSpan.Zero
        };

        var address = AddressExtensions.CreateMeshAddress($"attach-retry-{Guid.NewGuid():N}");
        using var registration = routing.RegisterStream(address, (d, _) => Observable.Return(d));

        // The completion task exists the moment RegisterStream returns; capture it BEFORE opening
        // the gate so there is no window in which the attach could finish unobserved.
        var settled = routing.AttachSettled(address)
                      ?? throw new InvalidOperationException(
                          "RegisterStream did not record an attach completion for the address — the "
                          + "seam this test waits on is gone, and a poll would silently take its place.");

        // Open the gate exactly the way Orleans does: the lifecycle observer's OnStart at Active.
        await ((ILifecycleObserver)readiness).OnStart(CancellationToken.None);

        // 🚨 Wait for the attach to TERMINATE, never for the attempt counter to stop moving (#2793).
        // The retry hops through the thread-pool scheduler between attempts, so on a loaded shard a
        // settle-by-silence poll reads the count during a hop and declares it final — and the value
        // it reads is 1, which is exactly the regression signature this file exists to detect. The
        // condition under test is "the attach gave up", and this task IS that condition. The bound
        // is a backstop against a hang, not the measurement: with AttachBackoff collapsed to zero
        // the real elapsed time is milliseconds.
        await settled.WaitAsync(TimeSpan.FromSeconds(30));

        return provider.GetStreamCalls;
    }

    /// <summary>
    /// 🚨 THE REGRESSION. On unfixed code this observes exactly 1 — the single attempt that latched
    /// the hub into "cross-process routing DISABLED" — instead of the full bounded budget.
    /// </summary>
    [Fact]
    public async Task TransientAttachFailure_IsRetried_UpToTheBudget()
    {
        var attempts = await AttachAttemptsAsync(DirectoryRejection);

        attempts.Should().Be(
            OrleansRoutingService.SubscribeAttachRetries + 1,
            "a grain-directory rejection is TRANSIENT — Orleans' own message says it reconnects in "
            + "under a second — and the delivery leg already retries this exact exception class. "
            + "One attempt is what latched a hub into permanently disabled cross-process routing "
            + "on every rolling deploy (#2633)");
    }

    /// <summary>
    /// The other direction, and the one that keeps the retry honest: a fault that is not transient
    /// must NOT be re-attempted. A retry budget spent on a permanent failure is six times the log
    /// noise and six times the delay before the hub's outbound gate opens, for the same outcome.
    /// </summary>
    [Fact]
    public async Task NonTransientAttachFailure_StillGivesUpOnTheFirstAttempt()
    {
        var attempts = await AttachAttemptsAsync(
            () => new NotSupportedException("attach probe — a permanent failure must still fail"));

        attempts.Should().Be(1,
            "everything IsTransientFailure does not recognise is permanent, and a permanent "
            + "failure must still fail — loudly, once, on the first attempt");
    }

    /// <summary>
    /// The policy itself, with no host at all: bounded, growing, and capped. This is what stops the
    /// retry from becoming the unbounded loop the codebase forbids.
    /// </summary>
    [Fact]
    public void AttachBackoff_IsBoundedAndCapped()
    {
        OrleansRoutingService.SubscribeAttachRetries.Should().BeInRange(1, 10,
            "a retry budget must be small enough to stay inside the caller-visible budgets above it");

        var waits = Enumerable.Range(0, OrleansRoutingService.SubscribeAttachRetries)
            .Select(OrleansRoutingService.SubscribeAttachBackoff)
            .ToArray();

        waits.Should().BeInAscendingOrder("backoff must grow, or it is a busy loop with a sleep in it");
        waits.Should().OnlyContain(w => w <= TimeSpan.FromSeconds(4),
            "the backoff is capped so the total budget stays predictable");
        waits.Sum(w => w.TotalSeconds).Should().BeLessThan(30,
            "the whole budget must finish well inside the 120 s readiness gate that precedes it and "
            + "the 60 s request budgets that sit above it");
    }
}
