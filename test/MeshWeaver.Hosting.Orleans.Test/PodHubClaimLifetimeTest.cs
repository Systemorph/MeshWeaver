using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #1742's stated open residual — "a claim that fails to land degrades SILENTLY back to
/// the stream" — at the seam that produced it.</b>
///
/// <para><b>The defect.</b> <c>OrleansRoutingService.AttachPodHub</c> claims an address for THIS
/// process so the rest of the cluster can reach its hub with a directed <c>IPodHubGrain.Deliver</c>
/// call instead of an Orleans memory-stream publish. The claim carried a BUDGET — five retries over
/// ≈3 s — and on exhausting it logged a <c>Debug</c> line and stopped. After that a SILO-hosted hub
/// kept the stream transport for the whole life of the process, with no signal anywhere on the
/// owning side; the only trace was a router-side fallback line in a different pod's log.</para>
///
/// <para><b>The rule.</b> #2426: no lifetime that depends on a message a restarting process would
/// never send, and none that expires on a counter either. The claim's terminals are now DERIVED —
/// the hub's registration being disposed, or <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// — plus one that is genuine IMPOSSIBILITY rather than a budget: a process that cannot host a grain
/// can never win the claim, so there the initial budget IS the end and the give-up stays at
/// <c>Debug</c> because it is the expected permanent outcome.</para>
///
/// <para><b>No cluster, no clock, no mocks of anything of ours.</b> The subject is the real
/// <see cref="OrleansRoutingService"/>; <see cref="RefusingGrainFactory"/> is a RECORDER whose
/// pod-hub grain answers <c>false</c> — the exact answer <c>PodHubGrain.Attach</c> gives when the
/// activation lands on a silo with no local route for the address — and the backoff is collapsed
/// through the instance seam so the POLICY is what is measured. The stopping signal is a real
/// <see cref="IHostApplicationLifetime"/>, cancelled through the same
/// <see cref="IHostApplicationLifetime.StopApplication"/> the runtime calls on SIGTERM.</para>
///
/// <para><b>Fails on unfixed code:</b>
/// <see cref="AClaimThatCannotLand_WhereGrainsCanBeHosted_KeepsRetrying_AndWarnsExactlyOnce"/>
/// observes six attempts and zero Warning lines.</para>
///
/// <para>See <c>Doc/Architecture/DurableStreamsViaMeshNodes</c>.</para>
/// </summary>
public class PodHubClaimLifetimeTest
{
    private static readonly Address Hub = new("portal", "claim-lifetime");

    /// <summary>The gap that separates "still retrying" from "gave up", with margin.</summary>
    private const int BeyondTheBudget = OrleansRoutingService.PodHubAttachRetries + 5;

    private static IObservable<IMessageDelivery> Ignore(IMessageDelivery d, CancellationToken _) =>
        Observable.Return(d);

    private static Microsoft.Extensions.DependencyInjection.ServiceProvider Services(
        IHostApplicationLifetime? lifetime = null)
    {
        var services = new ServiceCollection();
        // Registered but never fired: the stream attach then stays parked on the #1129 readiness
        // gate, so this test needs no stream provider and touches none.
        services.AddSingleton(new OrleansStreamingReadiness());
        if (lifetime is not null)
            services.AddSingleton(lifetime);
        return services.BuildServiceProvider();
    }

    private static OrleansRoutingService Router(
        RefusingGrainFactory factory, IServiceProvider sp, RecordingLogger logger, bool canHostGrains) =>
        new(factory, sp, logger)
        {
            // Instance seams, never static state: the POLICY is under test, not the clock, and not
            // whether a silo happens to be running underneath.
            CanHostGrains = canHostGrains,
            ClaimBackoff = _ => TimeSpan.FromMilliseconds(1),
        };

    /// <summary>Bounded wait on a POSITIVE signal: the claim reached at least this many attempts.</summary>
    private static async Task<int> WaitForAttempts(RefusingGrainFactory factory, int target)
    {
        for (var i = 0; i < 400; i++)
        {
            var now = factory.AttachCalls;
            if (now >= target)
                return now;
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"the claim stopped at {factory.AttachCalls} attempt(s), short of {target} — it gave up");
    }

    /// <summary>
    /// 🚨 Waits for the claim to TERMINATE, never for the attempt counter to stop moving (#2793).
    /// The retry hops through the thread-pool scheduler between attempts, so on a loaded CI shard
    /// that hop exceeds a 25 ms poll and "two equal readings" reads the count MID-HOP — it returned
    /// 1 on the merge-queue entry for #2800, which is precisely the value unfixed code produces, so
    /// the false RED spelled the regression's own signature. <c>PodHubClaimSettled</c> is the
    /// condition itself, positively; the bound is a backstop against a hang, not the measurement
    /// (with the backoff collapsed to 1 ms the real elapsed time is milliseconds).
    /// </summary>
    private static async Task<int> WaitUntilSettled(OrleansRoutingService routing, RefusingGrainFactory factory)
    {
        var settled = routing.PodHubClaimSettled(Hub)
                      ?? throw new InvalidOperationException(
                          "the routing service recorded no pod-hub claim for the address — the seam "
                          + "this test waits on is gone, and a poll would silently take its place.");
        await settled.Timeout(TimeSpan.FromSeconds(30)).LastOrDefaultAsync();
        return factory.AttachCalls;
    }

    /// <summary>
    /// 🚨 THE REGRESSION. On unfixed code the claim makes exactly
    /// <c>PodHubAttachRetries + 1</c> attempts and then leaves a silo-hosted hub on the Orleans
    /// stream forever, saying so only at <c>Debug</c>.
    /// </summary>
    [Fact]
    public async Task AClaimThatCannotLand_WhereGrainsCanBeHosted_KeepsRetrying_AndWarnsExactlyOnce()
    {
        var factory = new RefusingGrainFactory();
        var logger = new RecordingLogger();
        await using var sp = Services();
        using var routing = Router(factory, sp, logger, canHostGrains: true);

        using var registration = routing.RegisterStream(Hub, Ignore);
        var attempts = await WaitForAttempts(factory, BeyondTheBudget);

        attempts.Should().BeGreaterThan(OrleansRoutingService.PodHubAttachRetries + 1,
            "the claim's lifetime is the HUB's, not a counter — a bounded claim that gave up left a "
            + "silo-hosted hub on the stream transport for the life of the process, invisibly, which "
            + "is #1742's stated open residual");

        var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle(
            "a silo that cannot claim its own hub is abnormal and must be named ONCE — a line per "
            + "attempt would be the log storm #2426/#2546 exist to remove");
        warnings[0].Message.Should().Contain(Hub.ToString())
            .And.Contain("did not land",
                "the Loki gate in Doc/Architecture/DurableStreamsViaMeshNodes matches on "
                + "\"Pod-hub claim for\" and \"did not land\"");
        logger.Records.Should().NotContain(
            r => r.Level == LogLevel.Debug && r.Message.Contains("did not land in this process"),
            "the give-up line belongs to a process that cannot host a grain — this one can, and it "
            + "has not given up");
    }

    /// <summary>
    /// The other direction, and what keeps the rule from becoming an unbounded poll: a process that
    /// cannot host a grain can NEVER win this claim (<c>PodHubGrain</c> is
    /// <c>[PreferLocalPlacement]</c>, so from a cluster client the activation lands on some silo
    /// that has no local route and answers <c>false</c>, for ever). That is impossibility, not a
    /// budget — and retrying it would cost the SILO one <c>Information</c> line per hub per backoff
    /// interval, which is the storm shape this codebase removes rather than adds.
    /// </summary>
    [Fact]
    public async Task AClaimThatCannotLand_WhereNoGrainCanBeHosted_StopsAtTheInitialBudget_AtDebug()
    {
        var factory = new RefusingGrainFactory();
        var logger = new RecordingLogger();
        await using var sp = Services();
        using var routing = Router(factory, sp, logger, canHostGrains: false);

        using var registration = routing.RegisterStream(Hub, Ignore);
        var attempts = await WaitUntilSettled(routing, factory);

        attempts.Should().Be(OrleansRoutingService.PodHubAttachRetries + 1,
            "an Orleans client cannot host a grain, so the claim is impossible rather than slow — "
            + "spinning on it converges on nothing and costs the silo a log line per attempt");
        logger.Records.Should().NotContain(r => r.Level == LogLevel.Warning,
            "there it is the EXPECTED permanent outcome — a per-hub warning would be pure noise, "
            + "which is why the level is derived from what this process can host and never from the "
            + "attempt count");
        logger.Records.Should().Contain(
            r => r.Level == LogLevel.Debug && r.Message.Contains("did not land"),
            "the outcome is still on the record, at the level it deserves");
    }

    /// <summary>
    /// TERMINAL 1 — the hub's registration. The claim is alive exactly as long as the hub it claims
    /// for; disposing the registration ends it, which is also the path <c>Dispose</c> takes through
    /// the in-flight composite.
    /// </summary>
    [Fact]
    public async Task DisposingTheRegistration_EndsTheClaim()
    {
        var factory = new RefusingGrainFactory();
        await using var sp = Services();
        using var routing = Router(factory, sp, new RecordingLogger(), canHostGrains: true);

        var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttempts(factory, BeyondTheBudget);

        registration.Dispose();

        // Let any attempt already in flight land, then assert the count is STILL — a negative
        // "nothing more happened" with no positive signal to filter for.
        await Task.Delay(250);
        var afterDisposal = factory.AttachCalls;
        await Task.Delay(400);
        factory.AttachCalls.Should().Be(afterDisposal,
            "an indefinite claim whose terminal is not wired is an unbounded poll — the whole point "
            + "of a DERIVED lifetime is that the hub going away ends it");
    }

    /// <summary>
    /// TERMINAL 2 — the host. <see cref="IHostApplicationLifetime.ApplicationStopping"/> fires
    /// strictly before the Orleans silo hosted service stops, and the claim re-reads it on every
    /// re-subscribe (the gate lives inside the <c>Defer</c>), so a claim still bouncing when
    /// shutdown begins stops asking instead of placing an activation on the silo that is leaving.
    /// </summary>
    [Fact]
    public async Task TheHostBeginningToStop_EndsTheClaim()
    {
        // A real host purely for its real ApplicationLifetime — nothing is started; StopApplication
        // fires ApplicationStopping regardless, and that token is the signal under test.
        var lifetime = new HostBuilder().Build().Services.GetRequiredService<IHostApplicationLifetime>();
        var factory = new RefusingGrainFactory();
        await using var sp = Services(lifetime);
        using var routing = Router(factory, sp, new RecordingLogger(), canHostGrains: true);

        using var registration = routing.RegisterStream(Hub, Ignore);
        await WaitForAttempts(factory, BeyondTheBudget);

        lifetime.StopApplication();

        await Task.Delay(250);
        var afterStopping = factory.AttachCalls;
        await Task.Delay(400);
        factory.AttachCalls.Should().Be(afterStopping,
            "claiming an address for a process that is going away only places an activation on the "
            + "silo that is leaving — the indefinite retry must not re-open that window");
    }

    /// <summary>
    /// The policy itself, with no host at all: the backoff grows, is capped, and the initial budget
    /// is small enough to be a REPORTING threshold rather than a wait anybody notices.
    /// </summary>
    [Fact]
    public void TheClaimBackoff_GrowsAndIsCapped()
    {
        OrleansRoutingService.PodHubAttachRetries.Should().BeInRange(1, 10,
            "this is the point at which a claim becomes reportable, not a budget anything waits out");

        var waits = Enumerable.Range(0, OrleansRoutingService.PodHubAttachRetries + 1)
            .Select(OrleansRoutingService.PodHubClaimBackoff)
            .ToArray();

        waits.Should().BeInAscendingOrder("backoff must grow, or it is a busy loop with a sleep in it");
        waits.Should().OnlyContain(w => w <= TimeSpan.FromSeconds(2),
            "an indefinite retry MUST be capped — the ceiling is what bounds its steady-state cost");
        waits[^1].Should().Be(TimeSpan.FromSeconds(2),
            "the attempt index is clamped at the budget, so a claim that keeps retrying settles "
            + "exactly at the ceiling instead of growing without bound");
    }

    /// <summary>
    /// The pod-hub grain a claim that cannot land meets: <c>Attach</c> answers <c>false</c>, which
    /// is verbatim what <c>PodHubGrain.Attach</c> returns when the activation lands on a silo that
    /// has no local route for the address. Counting happens on the GRAIN, so a <c>Detach</c> during
    /// teardown can never be mistaken for another attempt.
    /// </summary>
    private sealed class RefusingPodHubGrain : IPodHubGrain
    {
        private int attachCalls;
        public int AttachCalls => Volatile.Read(ref attachCalls);

        public Task<bool> Attach()
        {
            Interlocked.Increment(ref attachCalls);
            return Task.FromResult(false);
        }

        public Task Detach() => Task.CompletedTask;

        public Task<IMessageDelivery> Deliver(IMessageDelivery delivery) => Task.FromResult(delivery);
    }

    /// <summary>
    /// Hands out the one refusing pod-hub grain. Only the string-key overload is implemented
    /// because it is the only shape the mesh uses — every other member throws, so a new call shape
    /// fails loudly here instead of passing silently. Same recorder technique as
    /// <c>SiloShutdownActivationGateTest.RecordingGrainFactory</c>.
    /// </summary>
    private sealed class RefusingGrainFactory : IGrainFactory
    {
        private readonly RefusingPodHubGrain podHub = new();
        private readonly ConcurrentQueue<string> keys = new();

        public int AttachCalls => podHub.AttachCalls;
        public IReadOnlyList<string> Keys => keys.ToArray();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            keys.Enqueue(primaryKey);
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
    /// Captures what the router logged, at which level — the assertion here is over the LEVEL, so a
    /// null logger would make every claim in this file vacuous.
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
