using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 <b>Issue #2638 — the silo stop must not run over routing work this silo has already
/// accepted.</b> The open question #2647 left was WHERE in the silo host's stop sequence the Autofac
/// root goes relative to routing quiescence, and whether a lifecycle hook can hold it without
/// deadlocking. The answer, from source (Orleans 10.2.2 <c>Silo.Participate</c> /
/// <c>LifecycleSubject.OnStop</c>, <c>MeshTeardownHostedService</c>, <c>MeshHostApplicationBuilder</c>):
/// the container is disposed by the HOST after every hosted service — the silo included — has
/// stopped and <c>StoppedAsync</c> has drained the mesh; and NOTHING in that sequence ever waited
/// on a route leg, because <c>RoutingGrain.RouteMessage</c> is O(1) (Orleans' deactivation wait
/// sees no request), the routing pool holds a leg's permit only for its subscribe, and the per-node
/// delivery plus every NACK were detached from even that.
///
/// <para><b>The seam.</b> <see cref="RoutingQuiescence"/> counts every leg and NACK from dispatch
/// to termination, and <see cref="RoutingQuiescenceSiloParticipant"/> holds the silo stop at
/// <see cref="ServiceLifecycleStage.Active"/> — before membership announces <c>ShuttingDown</c>
/// and before <c>Catalog.DeactivateAllActivations</c> at
/// <see cref="ServiceLifecycleStage.GrainDeactivation"/> — until that count is zero, bounded.</para>
///
/// <para><b>Real Orleans lifecycle machinery, no cluster, no mocks, no timing.</b> The stop is
/// driven through a real <see cref="SiloLifecycleSubject"/> — the exact type
/// <c>Silo.StopAsync</c> awaits — with a probe subscribed at the grain-deactivation stage that
/// records what it found in flight when its turn came. Every wait is on the gauge's own emissions
/// or bounded by a budget; nothing sleeps.</para>
/// </summary>
public class RoutingQuiescenceTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<RoutingQuiescenceSiloParticipant>
    {
        private readonly List<Entry> entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (entries) return entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries) entries.Add(new Entry(logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Stands in for <c>Catalog.DeactivateAllActivations</c>: subscribed at the stage the silo
    /// deactivates its grains, it records the routing gauge as it found it when its stop ran. Reads
    /// the REPLAYED count, which is the value the hold itself acts on.
    /// </summary>
    private sealed class GrainDeactivationProbe(RoutingQuiescence quiescence) : ILifecycleObserver
    {
        private int stopped;
        private int inFlightWhenStopped = -1;

        public bool Stopped => Volatile.Read(ref stopped) != 0;
        public int InFlightWhenStopped => Volatile.Read(ref inFlightWhenStopped);

        public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnStop(CancellationToken cancellationToken) =>
            quiescence.InFlightChanges
                .Take(1)
                .Do(inFlight =>
                {
                    Volatile.Write(ref inFlightWhenStopped, inFlight);
                    Volatile.Write(ref stopped, 1);
                })
                .Await();
    }

    private static async Task<int> WaitForCount(RoutingQuiescence quiescence, int expected) =>
        await quiescence.InFlightChanges
            .Where(n => n == expected)
            .FirstAsync()
            .Await(new CancellationTokenSource(Bound).Token);

    /// <summary>
    /// 🚨 THE ORDERING. With a leg in flight the silo stop does not reach grain deactivation; once
    /// the leg terminates it does, and finds nothing in flight. Both halves are positive
    /// assertions: the stop Task is provably incomplete while the slot is held (nothing can
    /// complete it — the gauge reads 1 and the budget is 30 s), and the probe records the count it
    /// actually observed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_HoldsBeforeGrainDeactivation_UntilTheRoutingWorkHasLanded()
    {
        using var quiescence = new RoutingQuiescence();
        var participant = new RoutingQuiescenceSiloParticipant(
            quiescence, NullLogger<RoutingQuiescenceSiloParticipant>.Instance);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        participant.Participate(lifecycle);
        var probe = new GrainDeactivationProbe(quiescence);
        lifecycle.Subscribe(nameof(GrainDeactivationProbe), ServiceLifecycleStage.GrainDeactivation, probe);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        // One route leg dispatched and not yet terminated — the gauge has SEEN it (replayed 1)
        // before the stop begins, so the pre-fix failure is deterministic, not raced.
        var leg = quiescence.Track();
        (await WaitForCount(quiescence, 1)).Should().Be(1);

        var stop = lifecycle.OnStop(TestContext.Current.CancellationToken);

        stop.IsCompleted.Should().BeFalse(
            "the silo stop must be HELD at stage Active while a route leg is in flight — without the "
            + "hold every stage completes synchronously and the grains deactivate over the leg (#2638)");
        probe.Stopped.Should().BeFalse(
            "grain deactivation runs at a LOWER stage than the hold, so it cannot have run yet");

        // The leg lands.
        leg.Dispose();

        await stop.WaitAsync(Bound);

        probe.Stopped.Should().BeTrue("once the hold releases, the stop proceeds through every lower stage");
        probe.InFlightWhenStopped.Should().Be(0,
            "grain deactivation must find NO routing work in flight — that is the whole invariant: a "
            + "leg finishes against live hubs, a live transport and a live container, never after them");
    }

    /// <summary>
    /// The control: an idle silo does not wait at all, and grain deactivation still runs.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_WithNothingInFlight_DoesNotHold()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        participant.Participate(lifecycle);
        var probe = new GrainDeactivationProbe(quiescence);
        lifecycle.Subscribe(nameof(GrainDeactivationProbe), ServiceLifecycleStage.GrainDeactivation, probe);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        await lifecycle.OnStop(TestContext.Current.CancellationToken).WaitAsync(Bound);

        probe.Stopped.Should().BeTrue();
        probe.InFlightWhenStopped.Should().Be(0);
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Information && e.Message.Contains("no routing work in flight", StringComparison.Ordinal),
            "an idle stop must SAY it had nothing to hold for — silence is indistinguishable from a hold that never ran");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    /// <summary>
    /// 🚨 Bounded, and the expiry is DATA. A leg that will not land inside the budget must not hang
    /// the silo stop — and the residual is named at Error, because that leg is the defect and this
    /// line is the only attribution its tail will get when it later dies on a stopping transport.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_BudgetExpiry_ReleasesTheSilo_AndNamesTheResidual()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger, TimeSpan.FromMilliseconds(200));

        // A leg that never terminates.
        using var stuck = quiescence.Track();
        (await WaitForCount(quiescence, 1)).Should().Be(1);

        await ((ILifecycleObserver)participant).OnStop(TestContext.Current.CancellationToken).WaitAsync(Bound);

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error
                 && e.Message.Contains("1 route leg(s) did not land", StringComparison.Ordinal),
            "the residual must be reported at Error, naming how many legs the silo is proceeding over");
    }

    /// <summary>
    /// 🚨 The residual must name WHICH leg, not just how many (#2833).
    ///
    /// <para>#2833 reports one leg outliving the budget and is explicit about why it went nowhere:
    /// <i>"the message names no target, sender, or delivery id and carries no exception or stack"</i>
    /// — leaving "high confidence on the class of defect, low on the specific leg". This
    /// participant runs ONLY at silo stop, so an occurrence cannot be reproduced on demand:
    /// whatever the line does not say is lost until the next shutdown.</para>
    ///
    /// <para>Fail-without: the count only. Pass-with: the label the leg was tracked under.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_BudgetExpiry_NamesTheStuckLeg_NotJustHowMany()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger, TimeSpan.FromMilliseconds(200));

        using var stuck = quiescence.Track("dispatch → acme/Stuck (delivery abc123)");
        (await WaitForCount(quiescence, 1)).Should().Be(1);

        await ((ILifecycleObserver)participant).OnStop(TestContext.Current.CancellationToken).WaitAsync(Bound);

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error
                 && e.Message.Contains("acme/Stuck", StringComparison.Ordinal)
                 && e.Message.Contains("abc123", StringComparison.Ordinal),
            "a count cannot be chased — the residual must carry the leg's target and delivery id, "
            + "because this line is the only record that will ever exist of that shutdown");
    }

    /// <summary>
    /// The sample is CAPPED, so a saturated silo cannot turn its own shutdown log into a flood —
    /// and the cap must say what it elided rather than silently truncating, or the reader cannot
    /// tell a complete list from a clipped one.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_ManyStuckLegs_SamplesThemAndSaysHowManyItElided()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger, TimeSpan.FromMilliseconds(200));

        var legs = new List<IDisposable>();
        for (var i = 0; i < 25; i++)
            legs.Add(quiescence.Track($"dispatch → acme/Leg{i}"));
        (await WaitForCount(quiescence, 25)).Should().Be(25);

        try
        {
            await ((ILifecycleObserver)participant).OnStop(TestContext.Current.CancellationToken).WaitAsync(Bound);

            logger.Entries.Should().Contain(
                e => e.Level == LogLevel.Error
                     && e.Message.Contains("25 route leg(s) did not land", StringComparison.Ordinal)
                     && e.Message.Contains("+15 more", StringComparison.Ordinal),
                "25 legs must report 10 by name and admit the other 15 — a truncation the reader "
                + "cannot see is worse than no sample at all");
        }
        finally
        {
            foreach (var leg in legs) leg.Dispose();
        }
    }

    /// <summary>
    /// A NON-graceful stop (the token already cancelled — <c>Silo.Dispose()</c>'s path, on which
    /// Orleans' own Active-stage stop returns immediately) holds nothing and says what it is
    /// abandoning.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_NonGraceful_DoesNotHold_AndReportsWhatItAbandons()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger);
        using var leg = quiescence.Track();
        (await WaitForCount(quiescence, 1)).Should().Be(1);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await ((ILifecycleObserver)participant).OnStop(cancelled.Token).WaitAsync(Bound);

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("NON-gracefully", StringComparison.Ordinal));
    }

    /// <summary>
    /// The host's own shutdown budget expiring MID-hold releases the silo too — Orleans keeps
    /// stopping through its stages on a cancelled token, and a participant that ignored it would
    /// hold the whole stop hostage to a leg the host has already given up on.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task SiloStop_HostBudgetExpiringMidHold_ReleasesTheSilo()
    {
        using var quiescence = new RoutingQuiescence();
        var logger = new CapturingLogger();
        var participant = new RoutingQuiescenceSiloParticipant(quiescence, logger);
        using var leg = quiescence.Track();
        (await WaitForCount(quiescence, 1)).Should().Be(1);

        using var hostBudget = new CancellationTokenSource();
        var stop = ((ILifecycleObserver)participant).OnStop(hostBudget.Token);
        stop.IsCompleted.Should().BeFalse("the hold is engaged: one leg in flight, a 30 s budget");

        hostBudget.Cancel();

        await stop.WaitAsync(Bound);
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("shutdown budget expired", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 The per-node delivery is PART OF THE LEG now. Pre-#2638 it was subscribed inside a
    /// <c>Select</c> and its subscription discarded, so the leg "terminated" the moment path
    /// resolution emitted while the grain call, its retries and its NACK ran detached — the tail
    /// that outlived the container in prod. The composed leg must stay in flight until the grain
    /// call has actually answered.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheGrainDelivery_IsPartOfTheLeg_WhichTerminatesOnlyOnceTheCallHasAnswered()
    {
        var answered = new TaskCompletionSource<IMessageDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);

        var leg = RoutingGrain.DeliverToGrainRoute(
                grainCall: () => answered.Task,
                grainKey: "messagehub/Planning",
                addressPath: "Planning",
                deliveryId: "d-2638-leg",
                postFailureToSender: (_, _) => { },
                logger: NullLogger.Instance)
            .Await();

        leg.IsCompleted.Should().BeFalse(
            "the leg must remain in flight while the grain call is pending — a detached delivery is "
            + "exactly what no drain could see (#2638)");

        answered.SetResult(new MessageDelivery<string>());

        await leg.WaitAsync(Bound);
    }

    /// <summary>
    /// The NACK arm is inside the leg as well: a terminal fault NACKs the sender and THEN the leg
    /// terminates — never the other way round, and never a fault escaping the leg.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheGrainDelivery_NacksInsideTheLeg_ThenTerminates()
    {
        var nacks = new List<(string Message, ErrorType Type)>();

        await RoutingGrain.DeliverToGrainRoute(
                grainCall: () => Task.FromException<IMessageDelivery>(new InvalidOperationException("node type not registered")),
                grainKey: "X",
                addressPath: "X",
                deliveryId: "d-2638-nack",
                postFailureToSender: (m, t) => nacks.Add((m, t)),
                logger: NullLogger.Instance,
                backoff: _ => TimeSpan.Zero,
                scheduler: System.Reactive.Concurrency.Scheduler.Immediate)
            .Await()
            .WaitAsync(Bound);

        var nack = nacks.Should().ContainSingle().Subject;
        nack.Type.Should().Be(ErrorType.Failed);
        nack.Message.Should().Contain("node type not registered");
    }
}
