using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#5356: the overlay self-heal recycled an instance hub while that hub was
/// still initializing, and the disposal threw away the requests the activation had already
/// accepted.
///
/// <para><b>The production line</b> (memex-cloud, 2026-09-22, three in one millisecond):
/// <c>[DISPOSE-DISCARD] Hub Deployments/build is disposing with SubscribeRequest (… from cache/…)
/// still deferred; initialization gates closed at deferral: [DataContextInit,MeshNodeInit] …
/// This teardown was requested by itself … why: Overlay self-heal …</c>. The watcher is armed in
/// <c>WithInitialization</c>. With no type node in hand it fires on the first usable replay of the
/// NodeType stream, which can arrive before the gates open. The <see cref="DisposeRequest"/> it
/// posted is exempt from every gate, so it overtook the parked requests and disposal discarded
/// them.</para>
///
/// <para><b>The subject</b> is the recycle action the self-heal really calls,
/// <see cref="NodeTypeEnrichmentHelpers.OverlaySelfHealRecycle"/>, fired against a real hub whose
/// gate the test holds closed. The timing is not raced: the request is demonstrably parked before
/// the self-heal fires, and the gate is opened only once the recycle decision is seen parked
/// behind it. If the teardown begins first, the gate stays shut, as it did in production.
/// Opening it anyway would let the quiesce drain serve the request and hide the defect: a first
/// draft of this test did that and passed on the unfixed code.</para>
///
/// <para><b>Negative control, measured:</b> with the action posting its
/// <see cref="DisposeRequest"/> directly (the pre-fix body), 2 of 3 fail. The parked-first case
/// fails with the production sentence itself, <i>"ParkedRequest … was still deferred;
/// initialization gates closed at deferral: [overlay-instance-init] … requested by itself … why:
/// Overlay self-heal …"</i>. With the fix, 3 of 3 pass.</para>
/// </summary>
public class OverlaySelfHealRecycleDrainsParkedWorkTest : HubTestBase
{
    private const string Gate = "overlay-instance-init";
    private const string NodeType = "Hosting/Deployment";

    private readonly DiscardLog log = new();

    public OverlaySelfHealRecycleDrainsParkedWorkTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(log);
            l.AddFilter<DiscardLog>(null, LogLevel.Debug);
        });
    }

    private record ParkedRequest : IRequest<ParkedResponse>;

    private record ParkedResponse;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(ParkedRequest), typeof(ParkedResponse));

    /// <summary>
    /// An instance hub whose initialization the test holds open: the gate's predicate never
    /// admits anything, and only <see cref="IMessageHub.OpenGate"/> releases it. The handler
    /// answers, so a response can only come from the request having been SERVED.
    /// </summary>
    private IMessageHub Instance(IMessageHub host, Address address)
    {
        var instance = host.GetHostedHub(
            address,
            c => c.WithTypes(typeof(ParkedRequest), typeof(ParkedResponse))
                .WithInitializationGate(Gate, _ => false)
                .WithHandler<ParkedRequest>((h, d) =>
                {
                    h.Post(new ParkedResponse(), o => o.ResponseFor(d));
                    return d.Processed();
                }));
        instance.Should().NotBeNull();
        return instance!;
    }

    [Fact]
    public async Task ARequestParkedBeforeTheSelfHealFires_IsServed_AndTheHubStillRecycles()
    {
        var host = GetHost();
        var address = new Address("overlay-instance", "parked-first");
        var instance = Instance(host, address);

        var response = host
            .Observe<ParkedResponse>(new ParkedRequest(), o => o.WithTarget(address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        await WaitForDeferred(host, atLeast: 1);

        // The self-heal fires while the gate is still closed: the production ordering.
        NodeTypeEnrichmentHelpers.OverlaySelfHealRecycle(instance, NodeType, Logger)();

        // The gate is opened ONLY if the recycle decision parked behind it. If the teardown began
        // instead, the gate stays shut as it did in production, and the teardown runs to its end
        // with the request still parked.
        var parked = await RecycleDecisionParksOrTeardownBegins(host, instance, deferredWithIt: 2);
        if (parked)
            instance.OpenGate(Gate);

        var served = await response;
        served.Should().NotBeNull(
            "the request was ACCEPTED before the self-heal decided to recycle; the recycle must "
            + "run after it, not discard it (#5356)");
        parked.Should().BeTrue(
            "the self-heal's recycle must wait behind the closed gate, not start the teardown");

        await instance.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        log.At(LogLevel.Error).Should().BeEmpty(
            "no accepted delivery may be discarded by a recycle the hub decided on itself");
    }

    [Fact]
    public async Task ARequestParkedAfterTheSelfHealFires_IsServedToo()
    {
        var host = GetHost();
        var address = new Address("overlay-instance", "healed-first");
        var instance = Instance(host, address);

        // The self-heal fires first, while the hub is initializing …
        NodeTypeEnrichmentHelpers.OverlaySelfHealRecycle(instance, NodeType, Logger)();
        var parked = await RecycleDecisionParksOrTeardownBegins(host, instance, deferredWithIt: 1);

        // … and a client's request arrives behind it, still inside the activation window.
        var response = host
            .Observe<ParkedResponse>(new ParkedRequest(), o => o.WithTarget(address))
            .FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        if (parked)
        {
            await WaitForDeferred(host, atLeast: 2);
            instance.OpenGate(Gate);
        }

        var served = await response;
        served.Should().NotBeNull(
            "the gate restores the backlog in arrival order and the recycle's DisposeRequest is "
            + "posted only when its own turn runs, so it lands behind the request");
        parked.Should().BeTrue();

        await instance.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        log.At(LogLevel.Error).Should().BeEmpty();
    }

    /// <summary>
    /// Positive control: queuing the decision must not lose the recycle. On a hub that finished
    /// initializing nothing is deferred and the self-heal still tears it down.
    /// </summary>
    [Fact]
    public async Task OnAnInitializedHub_TheSelfHealStillRecycles()
    {
        var host = GetHost();
        var address = new Address("overlay-instance", "initialized");
        var instance = Instance(host, address);
        instance.OpenGate(Gate);

        var served = await host
            .Observe<ParkedResponse>(new ParkedRequest(), o => o.WithTarget(address))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        served.Should().NotBeNull();

        NodeTypeEnrichmentHelpers.OverlaySelfHealRecycle(instance, NodeType, Logger)();

        await instance.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        instance.IsDisposing.Should().BeTrue();
    }

    /// <summary>
    /// Which happens first after the self-heal fires: the recycle decision is parked behind the
    /// closed gate (the instance's deferred count reaches <paramref name="deferredWithIt"/>), or
    /// the instance starts its teardown. Both are observed; neither is timed.
    /// </summary>
    private static async Task<bool> RecycleDecisionParksOrTeardownBegins(
        IMessageHub host, IMessageHub instance, int deferredWithIt)
        => await instance.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Quiescing)
            .Select(_ => false)
            .Merge(DeferredReaches(host, deferredWithIt).Select(_ => true))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

    private static IObservable<long> DeferredReaches(IMessageHub host, int atLeast)
        => Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Where(_ => Regex.Matches(host.GetDisposalDiagnostics(), @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) >= atLeast));

    /// <summary>
    /// Polls the disposal diagnostics, which report <c>deferred=&lt;N&gt;</c> per hub including
    /// hosted ones, until the gated instance has parked at least <paramref name="atLeast"/>
    /// deliveries. It is the only hub in the test that defers.
    /// </summary>
    private static async Task WaitForDeferred(IMessageHub host, int atLeast)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .Select(_ => host.GetDisposalDiagnostics())
            .Where(snapshot => Regex.Matches(snapshot, @"deferred=(\d+)")
                .Any(m => int.Parse(m.Groups[1].Value) >= atLeast))
            .Take(1)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>Captures event 7301 (<c>[DISPOSE-DISCARD]</c>) at every level.</summary>
    private sealed class DiscardLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> lines = new();

        public IEnumerable<string> At(LogLevel level) =>
            lines.Where(l => l.Level == level).Select(l => l.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Capture(lines);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 7301)
                    lines.Enqueue((level, formatter(state, exception)));
            }
        }
    }
}
