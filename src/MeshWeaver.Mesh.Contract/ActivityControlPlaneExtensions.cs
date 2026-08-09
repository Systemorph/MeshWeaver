using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Test")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Helpers for the Activity Control Plane — the canonical pattern where the
/// owning hub watches its OWN <see cref="MeshNodeReference"/> stream for
/// <see cref="ActivityLog.RequestedStatus"/> patches and translates them into
/// internal state-machine transitions (cancel, restart, etc.). See
/// <c>Doc/Architecture/ActivityControlPlane.md</c>.
///
/// <para>
/// The Status / RequestedStatus pair decouples request from current state:
/// <c>Status</c> is "what's actually happening", <c>RequestedStatus</c> is
/// "what the user wants to happen". A consumer (UI button, automated control,
/// orchestrating script) patches <c>RequestedStatus</c> via
/// <see cref="MeshNodeStreamExtensions.UpdateMeshNode"/>; the hub picks up
/// the patch and reacts.
/// </para>
/// </summary>
public static class ActivityControlPlaneExtensions
{
    /// <summary>
    /// Subscribe to <paramref name="hub"/>'s own MeshNode stream, project
    /// <see cref="ActivityLog.RequestedStatus"/>, and invoke
    /// <paramref name="onRequestedStatus"/> whenever it changes (including the
    /// initial emission when the hub first observes its own activity content).
    ///
    /// <para>
    /// Returns an <see cref="IDisposable"/> the caller is expected to register
    /// with the hub's lifetime (typically via
    /// <c>hub.RegisterForDisposal(...)</c> from a
    /// <c>WithInitialization</c> callback). When the hub is disposed the
    /// subscription tears down with it.
    /// </para>
    ///
    /// <para>
    /// The handler runs on whatever scheduler the upstream stream emits on —
    /// usually the hub's own action block. Treat it as hub-reachable code:
    /// no <c>await</c>, compose via <c>IObservable</c> chains for follow-up
    /// work. See <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </para>
    /// </summary>
    /// <param name="hub">The owning hub — typically an Activity hub or a
    /// NodeType hub that runs operations on its own content.</param>
    /// <param name="onRequestedStatus">Callback invoked with the latest
    /// <see cref="ActivityStatus"/> request. <c>null</c> means there's no
    /// pending request (the activity content has no <c>RequestedStatus</c>
    /// or the field has been cleared after a transition).</param>
    /// <param name="logger">Optional logger; the subscription's <c>OnError</c>
    /// is forwarded here so a faulted control-plane subscription doesn't
    /// silently disappear.</param>
    public static IDisposable WatchControlPlane(
        this IMessageHub hub,
        Action<ActivityStatus?> onRequestedStatus,
        ILogger? logger = null)
    {
        if (hub is null) throw new ArgumentNullException(nameof(hub));
        if (onRequestedStatus is null) throw new ArgumentNullException(nameof(onRequestedStatus));

        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.ActivityControlPlane");

        var workspace = hub.GetWorkspace();

        // Self-healing: a faulted control-plane subscription must NOT leave the
        // activity unobserved (the same "observer dies before terminal" hole that
        // stranded mid-stream threads). On fault, re-establish after a short delay
        // — never a one-shot that silently dies. Mirrors
        // ThreadExecution.InitializeThreadLifecycle. A disposed guard + SerialDisposable
        // make the re-establish stop cleanly when the hub tears down. See
        // Doc/Architecture/ActivityControlPlane.md → "Recovery on activation".
        return SubscribeWithReEstablish(
            () => workspace.GetMeshNodeStream()
                .Select(node => node.ContentAs<ActivityLog>(hub.JsonSerializerOptions)?.RequestedStatus)
                .DistinctUntilChanged(),
            onRequestedStatus,
            hub.Address,
            logger,
            "ActivityControlPlane subscription");
    }

    /// <summary>
    /// Sibling of <see cref="WatchControlPlane"/> for job-orchestration cases where the
    /// trigger isn't a single status field but a more general "this state needs work
    /// done now" condition (e.g., thread has unprocessed user messages and isn't already
    /// executing). Pure reactive composition — no flags, no Throttle, no scheduler hops:
    ///
    /// <list type="number">
    ///   <item><description>Source: hub's own MeshNode stream.</description></item>
    ///   <item><description><c>DistinctUntilChanged</c> on a caller-supplied
    ///     fingerprint so the same actionable state isn't dispatched twice.</description></item>
    ///   <item><description><c>Where</c> on a caller-supplied predicate to filter to
    ///     states that warrant dispatch.</description></item>
    ///   <item><description><c>SelectMany</c> on a caller-supplied dispatch function
    ///     returning <c>IObservable&lt;Unit&gt;</c> — chained writes, posts, and acks
    ///     all compose as one observable per round.</description></item>
    /// </list>
    ///
    /// <para>
    /// The returned <see cref="IDisposable"/> tears the subscription down. Caller is
    /// expected to register it with the hub's lifetime — typically via
    /// <c>hub.RegisterForDisposal(...)</c> from a <c>WithInitialization</c> callback.
    /// </para>
    /// </summary>
    /// <typeparam name="TFingerprint">Fingerprint type — usually a value tuple of the
    /// dispatch-relevant fields. Equality semantics drive
    /// <see cref="System.Reactive.Linq.Observable.DistinctUntilChanged{TSource,TKey}(IObservable{TSource},Func{TSource,TKey})"/>.</typeparam>
    /// <param name="hub">The owning hub.</param>
    /// <param name="fingerprint">Project a MeshNode to a value that compares equal when
    /// the dispatchable state hasn't changed.</param>
    /// <param name="needsDispatch">Returns <c>true</c> when this MeshNode state warrants
    /// a dispatch round.</param>
    /// <param name="dispatch">Returns an <c>IObservable&lt;Unit&gt;</c> that emits when
    /// the round is fully committed. Errors propagate to the subscription's OnError;
    /// the subscription stays alive (next state change will re-dispatch).</param>
    /// <param name="logger">Optional logger; subscription faults are forwarded here.</param>
    public static IDisposable WatchSubmission<TFingerprint>(
        this IMessageHub hub,
        Func<MeshNode, TFingerprint> fingerprint,
        Func<MeshNode, bool> needsDispatch,
        Func<MeshNode, IObservable<System.Reactive.Unit>> dispatch,
        ILogger? logger = null)
    {
        if (hub is null) throw new ArgumentNullException(nameof(hub));
        if (fingerprint is null) throw new ArgumentNullException(nameof(fingerprint));
        if (needsDispatch is null) throw new ArgumentNullException(nameof(needsDispatch));
        if (dispatch is null) throw new ArgumentNullException(nameof(dispatch));

        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.JobOrchestration");

        // Atomic single-flight guard. The upstream `GetMeshNodeStream` can emit
        // multiple distinct fingerprints before any single dispatch's async
        // commit (CreateNodeRequest + workspace.Update) has landed; without
        // the guard each fires its own DispatchRound with a fresh response id.
        // Concat doesn't help because the inner dispatch completes synchronously
        // (DispatchRound is fire-and-forget). The flag is per-watcher (one per
        // hub), set on entry, cleared on dispatch completion — the next state
        // change re-evaluates `needsDispatch` against current state and either
        // dispatches a fresh round (legitimate next round) or drops (in-flight
        // round set IsExecuting=true).
        var dispatching = 0;

        // Self-healing: a faulted submission watcher must NOT die silently — if it
        // does, the hub stops draining pending input / dispatching the next round
        // and every observer waiting on the result parks forever (the live-path
        // sibling of the init-recovery deadlock). On fault we reset the single-flight
        // guard and re-establish after a short delay. Mirrors WatchControlPlane and
        // ThreadExecution.InitializeThreadLifecycle.
        return SubscribeWithReEstablish<System.Reactive.Unit>(
            () => hub.GetWorkspace().GetMeshNodeStream()
                .DistinctUntilChanged(fingerprint)
                .Where(needsDispatch)
                .Where(_ => System.Threading.Interlocked.CompareExchange(ref dispatching, 1, 0) == 0)
                .SelectMany(node => dispatch(node)
                    .Catch<System.Reactive.Unit, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "Submission dispatch failed on {Address}; next state change will retry",
                            hub.Address);
                        return System.Reactive.Linq.Observable.Empty<System.Reactive.Unit>();
                    })
                    .Finally(() => System.Threading.Interlocked.Exchange(ref dispatching, 0))),
            _ => { },
            hub.Address,
            logger,
            "Submission watcher",
            // Release the single-flight guard a faulted in-flight dispatch may have
            // left set, so the re-established watcher can dispatch.
            onTransientFault: () => System.Threading.Interlocked.Exchange(ref dispatching, 0));
    }

    /// <summary>
    /// Subscribes to <paramref name="source"/> and keeps a control-plane / submission
    /// watcher alive across <b>transient</b> faults by re-establishing after a short
    /// delay — but treats a <b>terminal</b> fault as terminal. This is THE subscription
    /// primitive for any watcher whose death would leave a hub half-alive (its process
    /// keeps running while its control plane is dead — the #891 wedges-to-zero violation);
    /// never subscribe such a watcher with a bare log-and-die <c>OnError</c> handler.
    ///
    /// <para>Three fault classes, in classification order:</para>
    /// <list type="number">
    ///   <item><description><b>Poisoned own content</b> — a
    ///     <see cref="MeshNodeStreamException"/> with
    ///     <see cref="MeshNodeErrorCode.Deserialization"/>: the watched node's own content
    ///     cannot be materialized. TERMINAL for re-establish purposes: the faulted stream
    ///     replays the same emission, so re-establishing is a 1 Hz poison loop, not a
    ///     recovery. Logged CRITICAL and surfaced through
    ///     <paramref name="onPoisonedContent"/> so the failure lands in a visible sink
    ///     (e.g. the compile park registry) instead of a single log line.</description></item>
    ///   <item><description><b>Own node gone</b> — a routing
    ///     <see cref="ErrorType.NotFound"/> <see cref="DeliveryFailureException"/>: the node
    ///     this watcher exists to observe is gone / unroutable. Re-establishing then just
    ///     re-issues a doomed cross-hub <c>SubscribeRequest</c> every second forever — the
    ///     prod compile-activity storm of 2026-06-10 (4999 NotFound round-trips through the
    ///     single RoutingGrain in 14 min, starving unrelated subscriptions). That is the
    ///     exact "resubscribe to recover from a state that shouldn't happen" pattern the
    ///     deleted 2026-06-08 watchdog was. STOP; the orphaned hub idle-disposes.</description></item>
    ///   <item><description><b>Everything else</b> — a genuinely transient fault (hub
    ///     hiccup, cross-hub delivery blip) where the node is still alive and must not be
    ///     left unobserved: re-establish after a short delay with a fresh
    ///     subscription.</description></item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">Element type produced by the observed source.</typeparam>
    /// <param name="source">Factory that (re-)creates the observable to watch; called once per
    /// establish attempt so each re-establish gets a fresh subscription.</param>
    /// <param name="onNext">Invoked for every element the source emits.</param>
    /// <param name="address">The hub/node address this watcher belongs to (used for log context and
    /// own-node-gone detection).</param>
    /// <param name="logger">Optional logger for transient-fault and terminal-stop diagnostics.</param>
    /// <param name="faultLogContext">Short human-readable name of the watcher, included in fault logs.</param>
    /// <param name="onTransientFault">Optional callback run before a transient re-establish, e.g. to
    /// release a single-flight guard a faulted in-flight dispatch may have left set.</param>
    /// <param name="scheduleReEstablish">Test seam for how a transient re-establish is
    /// scheduled. Production default is a 1 s <see cref="Observable.Timer(TimeSpan)"/>
    /// off the action block. Returns the <see cref="IDisposable"/> that CANCELS the pending
    /// schedule — the returned watcher disposes it, so tearing the watcher down also drops
    /// the timer off the process-wide <c>TimerQueue</c> (#991).</param>
    /// <param name="onPoisonedContent">Optional visible sink invoked (once, with the fault) when
    /// the watcher stops on poisoned own content — route it somewhere a user/operator can see
    /// (park registry, health surface), not just a log.</param>
    public static IDisposable SubscribeWithReEstablish<T>(
        Func<IObservable<T>> source,
        Action<T> onNext,
        Address address,
        ILogger? logger,
        string faultLogContext,
        Action? onTransientFault = null,
        Func<Action, IDisposable>? scheduleReEstablish = null,
        Action<Exception>? onPoisonedContent = null)
    {
        var serial = new System.Reactive.Disposables.SerialDisposable();
        // 🚨 #991 — the PENDING re-establish must be cancellable, and cancelled by teardown.
        // `Observable.Timer` parks its entry on the process-wide `TimerQueue`, which is a
        // strong GC root: for as long as the timer is pending it holds the tick closure →
        // the `Establish` delegate → THIS method's closure → `source` → whatever the caller's
        // factory captured (in prod the per-NodeType hub, via NodeTypeCompilationHelpers).
        // The old code DISCARDED the timer's IDisposable, so disposing the watcher only set
        // `disposed` and killed `serial` — a hub whose stream faulted within the last second
        // of its life stayed rooted past Mesh.Dispose() (the ClrMD chain on #991:
        // TimerQueue → …ObservableImpl.Timer+Single+_ → Action<Int64> → DisplayClass2_1 →
        // Action → DisplayClass2_0 → Func<IObservable<MeshNode>> → MessageHub[AccessAssignment]).
        // Teardown at mesh disposal is exactly when that fault happens, which is why the leak
        // sampled as an intermittent MeshHubDisposalLeakTest failure. Holding the schedule in a
        // SerialDisposable also makes the dispose race safe: a schedule handed over after
        // disposal is disposed on assignment.
        var pendingReEstablish = new System.Reactive.Disposables.SerialDisposable();
        var disposed = false;
        var schedule = scheduleReEstablish
            ?? (reEstablish => Observable.Timer(TimeSpan.FromSeconds(1)).Subscribe(_ => reEstablish()));

        void Establish()
        {
            if (disposed) return;
            serial.Disposable = source().Subscribe(
                onNext,
                ex =>
                {
                    if (IsPoisonedContent(ex))
                    {
                        // Terminal: the watched node's own content cannot be deserialized, and
                        // the stream replays it — a re-establish is a 1 Hz poison loop, not a
                        // recovery. Stop, scream, and surface through the visible sink; the
                        // cure is repairing the content (then recycling the hub re-activates
                        // fresh watchers).
                        logger?.LogCritical(ex,
                            "{Context} on {Address} STOPPED: own-node content cannot be deserialized — " +
                            "re-establishing would replay the same poisoned emission. The watcher stays " +
                            "down until the content is repaired and the hub re-activates (#891).",
                            faultLogContext, address);
                        onPoisonedContent?.Invoke(ex);
                        return;
                    }
                    if (IsOwnNodeGone(ex))
                    {
                        // Terminal: the node this watcher observes is gone/unroutable.
                        // Stopping (not re-establishing) is what prevents the storm.
                        logger?.LogWarning(ex,
                            "{Context}: own node {Address} is gone (NotFound) — watch stops, no re-establish",
                            faultLogContext, address);
                        return;
                    }
                    logger?.LogError(ex, "{Context} faulted on {Address} — re-establishing",
                        faultLogContext, address);
                    onTransientFault?.Invoke();
                    if (!disposed)
                        pendingReEstablish.Disposable = schedule(Establish);
                });
        }
        Establish();
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            disposed = true;
            // Drop the pending re-establish FIRST — that is the TimerQueue entry rooting
            // this whole closure graph (#991); `serial` only holds the live subscription.
            pendingReEstablish.Dispose();
            serial.Dispose();
        });
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or an inner exception) is a
    /// <see cref="MeshNodeStreamException"/> with
    /// <see cref="MeshNodeErrorCode.Deserialization"/> — the per-emission typing fault
    /// <c>MeshNodeStreamHandle</c>'s content-typing boundary raises when a node's persisted
    /// content cannot be materialized. Re-subscribing replays the same emission, so for a
    /// watcher this is terminal-with-visibility, never re-establish.
    /// </summary>
    internal static bool IsPoisonedContent(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is MeshNodeStreamException { Error.Code: MeshNodeErrorCode.Deserialization })
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or an inner exception) is a routing
    /// <see cref="ErrorType.NotFound"/> — the node a watcher subscribes to does not
    /// resolve. On a hub's OWN node this is terminal (the node is gone), so the watcher
    /// must not resubscribe. Prefers the typed <see cref="DeliveryFailure.ErrorType"/>;
    /// falls back to the canonical router message because the cross-hub failure is
    /// sometimes re-wrapped as a plain <see cref="DeliveryFailureException"/>(string)
    /// that drops the typed <see cref="DeliveryFailureException.Failure"/>.
    /// </summary>
    internal static bool IsOwnNodeGone(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DeliveryFailureException { Failure.ErrorType: ErrorType.NotFound })
                return true;
        }
        return ex.Message.Contains("No node found", StringComparison.Ordinal);
    }
}
