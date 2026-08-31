using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Counts the routing work this silo has ACCEPTED and not yet LANDED — every route leg
/// <see cref="RoutingGrain"/> dispatched off its turn, and every <c>DeliveryFailure</c> NACK it
/// is carrying — so the silo stop can hold until that work has terminated
/// (<see cref="RoutingQuiescenceSiloParticipant"/>). Issue #2638.
///
/// <para><b>Why nothing else measures this.</b> <c>RoutingGrain.RouteMessage</c> does O(1) work
/// and returns <c>Forwarded</c> (issue #1028), so from Orleans' point of view the routing grain
/// never has a request in flight: <c>Catalog.DeactivateAllActivations</c> deactivates it
/// instantly, and the <c>DeactivationTimeout</c> it waits on in-flight requests with never
/// applies. The leg itself runs on the routing <c>IIoPool</c>, but
/// <c>SubscribeThroughPool</c> holds its permit for the SUBSCRIBE window only — so
/// <c>IoPoolSiloTeardown</c>'s join at the end of the silo stop sees nothing once the leg has
/// continued past its subscribe (the same gap <see cref="MeshWeaver.Mesh.ActivityTracker"/>
/// closes for activities). And the per-node grain delivery used to be DETACHED from the leg
/// entirely — subscribed inside a <c>Select</c> with its subscription discarded — so its retry
/// timers and its NACK ran under no drain at all. The prod incident is exactly that tail
/// executing after the host had disposed its Autofac root: <c>GetGrain</c> resolving a codec
/// provider from a dead <c>LifetimeScope</c>.</para>
///
/// <para><b>Hold, do not cancel.</b> A message already accepted for routing must still land
/// (<c>OrleansRoutingService.GrainWhileRunning</c> documents why the drain is deliberately
/// ungated). The hold sits at <see cref="ServiceLifecycleStage.Active"/> — BEFORE membership
/// announces <c>ShuttingDown</c> and BEFORE the catalog deactivates a single grain — so a leg
/// finishes against live local hubs, a live transport and a live container, and its NACK, if it
/// needs one, is carried while there is still something to carry it. Bounded: a leg that cannot
/// land inside the budget is reported as the defect it is, and the silo proceeds.</para>
///
/// <para>Same reactive shape as <see cref="MeshWeaver.Mesh.ActivityTracker"/>: deltas in, running
/// count out, serialised by QUEUEING onto one scheduler — no lock, no <c>Interlocked</c>, no
/// async gate. Instance state owned by the mesh (never <c>static</c>).</para>
/// </summary>
public sealed class RoutingQuiescence : IDisposable
{
    private readonly Subject<int> deltas = new();
    private readonly EventLoopScheduler scheduler = new();
    private readonly IConnectableObservable<int> counts;
    private readonly IDisposable connection;
    private int disposed;

    // 🚨 What is in flight, not just HOW MANY. The count alone made the shutdown residual
    // undiagnosable: #2833 reports one leg outliving the budget and says so itself — "the message
    // names no target, sender, or delivery id and carries no exception or stack", so the confidence
    // was "high on the class of defect, low on the specific leg". A leg that cannot be named cannot
    // be found, and this participant only runs at silo stop, so the occurrence is not reproducible
    // on demand: whatever the log did not say is lost until the next shutdown.
    //
    // Instance state on a mesh-scoped singleton (never static), and a ConcurrentDictionary because
    // legs enter and leave from many threads — the one sanctioned mutable collection, as an
    // instance field.
    private readonly ConcurrentDictionary<long, string> inFlightLabels = new();
    private long ticket;

    /// <summary>Initializes a new instance of the <see cref="RoutingQuiescence"/> class.</summary>
    public RoutingQuiescence()
    {
        // Replay(1) so a late subscriber (the silo stop) sees the CURRENT count immediately rather
        // than waiting for the next change — a stop on an idle silo must not wait at all.
        counts = deltas
            .ObserveOn(scheduler)
            .Scan(0, (running, delta) => running + delta)
            .StartWith(0)
            .Replay(1);
        connection = counts.Connect();
    }

    /// <summary>
    /// Live count of routing work in flight, starting with the current value. Emits on every
    /// dispatch and every termination, so a consumer waits for zero without polling.
    /// </summary>
    public IObservable<int> InFlightChanges => counts;

    /// <summary>
    /// Completes once no routing work is in flight. Emits immediately when the silo is already idle.
    /// </summary>
    public IObservable<Unit> WhenIdle =>
        counts.Where(running => running == 0).Take(1).Select(_ => Unit.Default);

    /// <summary>
    /// Registers one piece of routing work as in flight. Dispose the returned handle when it
    /// TERMINATES — landed, NACK'd, or faulted — never when it was merely dispatched. A double
    /// dispose does not double-decrement.
    /// </summary>
    public IDisposable Track() => Track("unlabelled");

    /// <summary>
    /// Registers one piece of routing work as in flight, under a label that identifies it if it
    /// fails to land. Dispose the returned handle when it TERMINATES — landed, NACK'd, or faulted —
    /// never when it was merely dispatched. A double dispose does not double-decrement.
    ///
    /// <para>🚨 Pass something that identifies the LEG — a target path, a delivery id, a transport
    /// — because this label is the only thing the shutdown residual can name (#2833). The count
    /// tells you a leg is stuck; the label tells you which.</para>
    /// </summary>
    /// <param name="label">Identity of the work, e.g. <c>"pod-hub → acme/Foo (delivery abc123)"</c>.</param>
    /// <returns>A handle whose disposal marks the work terminated.</returns>
    public IDisposable Track(string label)
    {
        var id = Interlocked.Increment(ref ticket);
        inFlightLabels[id] = label;
        Push(1);
        return Disposable.Create(() =>
        {
            inFlightLabels.TryRemove(id, out _);
            Push(-1);
        });
    }

    /// <summary>
    /// A snapshot of the labels of the work currently in flight, capped so a saturated silo cannot
    /// turn its own shutdown log into a flood. The cap is on the REPORT, not on the tracking.
    /// </summary>
    /// <param name="max">Maximum labels to return.</param>
    /// <returns>Up to <paramref name="max"/> labels, and a count of any remainder.</returns>
    public (IReadOnlyList<string> Labels, int NotShown) InFlightSample(int max = 10)
    {
        var all = inFlightLabels.Values.ToArray();
        return all.Length <= max
            ? (all, 0)
            : (all.Take(max).ToArray(), all.Length - max);
    }

    // 🚨 Tolerant of a straggler AFTER the mesh is gone. This singleton is disposed with the
    // container, and the one thing left that can still call Track()/release on it is precisely a
    // routing tail that outlived everything — the #2638 shape. There is nothing to count it
    // against any more, and the alternative (a disposed Subject throwing ObjectDisposedException
    // out of a Finally) would turn a leg's own bookkeeping into a second fault. The Subject is
    // never disposed for the same reason: with the connection gone it has no observers, so a late
    // push is a no-op rather than a throw.
    private void Push(int delta)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        deltas.OnNext(delta);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        connection.Dispose();
        scheduler.Dispose();
    }
}

/// <summary>
/// Holds the silo stop, at <see cref="ServiceLifecycleStage.Active"/>, until the routing work
/// this silo accepted has terminated — see <see cref="RoutingQuiescence"/> for what is counted
/// and why nothing else drains it. Issue #2638.
///
/// <para><b>Stage, and why it is the FIRST thing the stop does.</b> Orleans stops observers in
/// DESCENDING stage order, and <see cref="ServiceLifecycleStage.Active"/> is the highest stage a
/// running silo has started, so this runs before <c>MembershipAgent</c> announces
/// <c>ShuttingDown</c> (<see cref="ServiceLifecycleStage.BecomeActive"/>), before
/// <c>Catalog.DeactivateAllActivations</c> (<see cref="ServiceLifecycleStage.GrainDeactivation"/>),
/// before the message centre stops accepting application messages
/// (<see cref="ServiceLifecycleStage.RuntimeServices"/>) and long before the host disposes its
/// root container. Every leg therefore finishes against exactly what it was dispatched against:
/// live local hubs, a live transport, a live DI container. Held any later, a leg could only be
/// re-placed on another silo (after grain deactivation), or fail (after the transport stopped) —
/// and a NACK attempted after the container is gone is the very incident.</para>
///
/// <para><b>What makes the wait converge.</b> The host fires
/// <c>IHostApplicationLifetime.ApplicationStopping</c> BEFORE it stops any hosted service, and the
/// silo is a hosted service — so by the time this runs, <c>OrleansRoutingService.DeliverMessage</c>
/// already refuses new dispatches as <c>ShuttingDown</c>. The count is monotone non-increasing
/// from here, and on a healthy silo it reaches zero in milliseconds; only a leg that is genuinely
/// not terminating burns the budget, and that is the defect the residual line names.</para>
///
/// <para><b>No deadlock.</b> The wait belongs to the silo lifecycle's own thread-pool task and
/// parks nothing a leg needs: legs run on pool and thread-pool threads, their grain calls are
/// answered by a scheduler that is still fully running, their NACKs land on hubs that are still
/// alive. The Task handed to Orleans is a subscription bridged ONCE at the boundary, exactly like
/// <c>IoPoolSiloTeardown.OnStop</c>. A non-graceful stop (the token already cancelled — Orleans'
/// own <c>OnActiveStop</c> returns immediately on that) holds nothing.</para>
/// </summary>
public sealed class RoutingQuiescenceSiloParticipant
    : ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver
{
    /// <summary>
    /// The hold budget. Matches <c>MeshTeardownHostedService</c> and <c>IoPoolSiloTeardown</c>:
    /// a leg's own bounds are of this order (<c>RoutingGrain.ResolveTimeout</c> is 30 s), so a leg
    /// still in flight at expiry is one that would not have landed in time anyway.
    /// </summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    private readonly RoutingQuiescence quiescence;
    private readonly ILogger<RoutingQuiescenceSiloParticipant> logger;
    private readonly TimeSpan budget;

    /// <summary>Creates the participant with the production budget.</summary>
    /// <param name="quiescence">The mesh-scoped routing gauge the stop holds on.</param>
    /// <param name="logger">Logger for the hold's outcome lines.</param>
    public RoutingQuiescenceSiloParticipant(
        RoutingQuiescence quiescence,
        ILogger<RoutingQuiescenceSiloParticipant> logger)
        : this(quiescence, logger, DefaultBudget)
    {
    }

    /// <summary>Test seam: the same participant with an explicit budget.</summary>
    internal RoutingQuiescenceSiloParticipant(
        RoutingQuiescence quiescence,
        ILogger<RoutingQuiescenceSiloParticipant> logger,
        TimeSpan budget)
    {
        this.quiescence = quiescence;
        this.logger = logger;
        this.budget = budget;
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle observer) =>
        // Active ⇒ the LAST stage to start and the FIRST to stop (Orleans stops in descending
        // stage order). See the type remarks for why it must be this early.
        observer.Subscribe(nameof(RoutingQuiescenceSiloParticipant), ServiceLifecycleStage.Active, this);

    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken) => Task.CompletedTask;

    // 🚨 NOT `async`, and nothing here awaits — the composition is handed back as ONE Task at the
    // boundary Orleans demands, and the completion arrives on whichever thread the last leg
    // unwinds on. Same argument, same shape, as IoPoolSiloTeardown.OnStop.
    //
    // 🚨 Bridged with ReactiveCompletion.ObserveCompletion, never `.ToTask()`. The gauge signals
    // idle on its own event-loop thread; `.ToTask()` would resume Orleans' LifecycleSubject.OnStop
    // INLINE there, and every lower stage's synchronous prologue — membership, the catalog, the
    // pool drain — would then run on the one thread the gauge needs to count anything at all.
    // ObserveCompletion completes with RunContinuationsAsynchronously, so the silo stop continues
    // on the thread pool and the gauge's thread goes straight back to counting.
    Task ILifecycleObserver.OnStop(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // A non-graceful stop: Orleans' own Active-stage stop returns immediately on this, and
            // so does the hold. Whatever is in flight is reported, not waited for.
            return quiescence.InFlightChanges
                .Take(1)
                .Do(inFlight =>
                {
                    if (inFlight != 0)
                        logger.LogWarning(
                            "RoutingQuiescence: the silo is stopping NON-gracefully with {InFlight} route "
                            + "leg(s) in flight — not holding; they will be abandoned by the pool drain.",
                            inFlight);
                })
                .Select(_ => Unit.Default)
                .ObserveCompletion(ReportLateFault);
        }

        var started = Stopwatch.GetTimestamp();
        return quiescence.InFlightChanges
            .Take(1)
            .SelectMany(inFlight =>
            {
                if (inFlight == 0)
                {
                    logger.LogInformation(
                        "RoutingQuiescence: no routing work in flight — the silo may deactivate its grains");
                    return Observable.Return(Unit.Default);
                }

                logger.LogInformation(
                    "RoutingQuiescence: holding the silo stop while {InFlight} route leg(s) land "
                    + "(budget {Budget}) — before membership announces ShuttingDown and before any "
                    + "grain deactivates, so each lands or is NACK'd over a live transport",
                    inFlight, budget);

                var landed = quiescence.WhenIdle
                    .Do(_ => logger.LogInformation(
                        "RoutingQuiescence: routing work landed after {Elapsed} ms — the silo may deactivate its grains",
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds))
                    .Timeout(budget)
                    // Expiry is DATA, not silence: read the residual off the gauge and name it. The
                    // silo proceeds — a stop that never releases is worse than one that releases
                    // loudly — and the leg that would not land is the defect to fix. Never widen
                    // the budget.
                    .Catch<Unit, TimeoutException>(_ => quiescence.InFlightChanges
                        .Take(1)
                        .Do(residual =>
                        {
                            // 🚨 Name the legs. This participant runs ONLY at silo stop, so an
                            // occurrence is not reproducible on demand — whatever this line does not
                            // say is lost until the next shutdown. #2833 is exactly that: one leg
                            // outlived the budget and the report could say nothing about which,
                            // leaving "high confidence on the class, low on the specific leg".
                            var (labels, notShown) = quiescence.InFlightSample();
                            logger.LogError(
                                "RoutingQuiescence: {Residual} route leg(s) did not land within {Budget} — "
                                + "the silo is proceeding to deactivate its grains over them. Each will now "
                                + "fail on its own bound against a stopping transport, and its NACK may be "
                                + "undeliverable. A leg that cannot land in {Budget} is stuck; find it, do "
                                + "not widen the budget. Stuck leg(s): {StuckLegs}{More}",
                                residual, budget, budget,
                                labels.Count == 0 ? "(none recorded)" : string.Join(" | ", labels),
                                notShown == 0 ? string.Empty : $" (+{notShown} more)");
                        })
                        .Select(_ => Unit.Default));

                // The host's own shutdown budget can expire mid-hold (HostOptions.ShutdownTimeout).
                // Orleans keeps stopping through stages on a cancelled token and so must this: a
                // registration on the token is the reactive bridge, and Amb takes whichever ends
                // the hold first.
                var hostGaveUp = Observable.Create<Unit>(observer =>
                        cancellationToken.Register(() => observer.OnNext(Unit.Default)))
                    .Take(1)
                    .SelectMany(_ => quiescence.InFlightChanges.Take(1))
                    .Do(residual =>
                    {
                        // Same reasoning as the budget-expiry arm: this is the other way the hold
                        // ends over live work, and it is just as unreproducible.
                        var (labels, notShown) = quiescence.InFlightSample();
                        logger.LogWarning(
                            "RoutingQuiescence: the host's shutdown budget expired with {Residual} route "
                            + "leg(s) still in flight after {Elapsed} ms — releasing the silo stop over "
                            + "them. Still in flight: {StuckLegs}{More}",
                            residual, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            labels.Count == 0 ? "(none recorded)" : string.Join(" | ", labels),
                            notShown == 0 ? string.Empty : $" (+{notShown} more)");
                    })
                    .Select(_ => Unit.Default);

                return landed.Amb(hostGaveUp);
            })
            .FirstAsync()
            .ObserveCompletion(ReportLateFault);
    }

    // The gauge is a replayed Scan over a Subject — it cannot fault after emitting — so this arm
    // exists for the contract, not for a case anyone expects; if it ever fires, say so.
    private void ReportLateFault(Exception ex) =>
        logger.LogError(ex,
            "RoutingQuiescence: the routing gauge faulted AFTER the silo stop had stopped waiting on it");
}

/// <summary>DI wiring for <see cref="RoutingQuiescence"/> and its silo lifecycle participant.</summary>
public static class RoutingQuiescenceExtensions
{
    /// <summary>
    /// Registers the mesh-scoped <see cref="RoutingQuiescence"/> gauge and the silo lifecycle
    /// participant that holds the silo stop on it (issue #2638). Idempotent. On an Orleans CLIENT
    /// host the participant is registered but never enumerated — a client has no silo lifecycle —
    /// so the registration is inert there, exactly like <c>IoPoolSiloTeardown</c>'s.
    /// </summary>
    /// <param name="services">The service collection to add the gauge and participant to.</param>
    /// <returns>The same service collection for further chaining.</returns>
    public static IServiceCollection AddRoutingQuiescence(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(RoutingQuiescence)))
            return services;
        services.AddSingleton<RoutingQuiescence>();
        services.AddSingleton<RoutingQuiescenceSiloParticipant>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp =>
            sp.GetRequiredService<RoutingQuiescenceSiloParticipant>());
        return services;
    }
}
