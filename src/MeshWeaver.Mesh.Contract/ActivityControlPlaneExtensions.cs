using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        var serial = new System.Reactive.Disposables.SerialDisposable();
        var disposed = false;
        void Establish()
        {
            if (disposed) return;
            serial.Disposable = workspace.GetMeshNodeStream()
                .Select(node => (node?.Content as ActivityLog)?.RequestedStatus)
                .DistinctUntilChanged()
                .Subscribe(
                    onRequestedStatus,
                    ex =>
                    {
                        logger?.LogError(ex,
                            "ActivityControlPlane subscription faulted on {Address} — re-establishing",
                            hub.Address);
                        if (!disposed)
                            Observable.Timer(TimeSpan.FromSeconds(1)).Subscribe(_ => Establish());
                    });
        }
        Establish();
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            disposed = true;
            serial.Dispose();
        });
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
        return hub.GetWorkspace().GetMeshNodeStream()
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
                .Finally(() => System.Threading.Interlocked.Exchange(ref dispatching, 0)))
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex,
                    "Submission watcher faulted on {Address}",
                    hub.Address));
    }
}
