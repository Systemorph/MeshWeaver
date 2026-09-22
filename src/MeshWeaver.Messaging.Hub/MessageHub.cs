using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.ServiceProvider;
using MeshWeaver.ShortGuid;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// The concrete <see cref="IMessageHub"/>: a single-threaded actor that processes
/// <see cref="IMessageDelivery"/> messages serially through a registered rule chain. Owns its
/// hosted child hubs, correlates request/response via AsyncSubject-backed callbacks, and runs a
/// reactive initialization and a phased reactive disposal (Quiescing → DisposeHostedHubs → ShutDown).
/// Sealed; constructed by the framework, not directly by application code.
/// </summary>
public sealed class MessageHub : IMessageHub
{
    /// <summary>This hub's address (its routing/partition key), taken from <see cref="Configuration"/>.</summary>
    public Address Address => Configuration.Address;


    /// <summary>
    /// Schedules <paramref name="action"/> onto the hub's action block by posting an execution
    /// request, so it runs serially with message handling; its async leaf runs off the action block.
    /// Faults are routed to <paramref name="exceptionCallback"/>.
    /// </summary>
    /// <param name="action">The work to run, receiving the hub's cancellation token.</param>
    /// <param name="exceptionCallback">Invoked with any exception thrown by <paramref name="action"/>.</param>
    public void InvokeAsync(Func<CancellationToken, Task> action, Func<Exception, Task> exceptionCallback) =>
        Post(new ExecutionRequest(action, exceptionCallback));

    /// <summary>The DI service provider scoped to this hub.</summary>
    public IServiceProvider ServiceProvider { get; }


    // Per-message response subjects. Observe(...) creates and stores; HandleCallbacks
    // pushes the response onto the matching subject. AsyncSubject emits the last value
    // on subscribe, so subscribers added before AND after the response arrives both see it.
    // Metadata (request type / target / age) is captured at registration so the dispose
    // Quiescing phase can name *which* callbacks are still pending when it times out.
    private readonly Dictionary<string, PendingCallback> responseSubjects = new();

    private sealed record PendingCallback(
        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> Subject,
        string RequestType,
        Address? Target,
        long RegisteredAtTicks,
        // 🚨 This hub's <see cref="Version"/> — its processed-message counter — at the moment the
        // wait began. It is what turns the timeout's idleness claim from an INSTANTANEOUS SAMPLE
        // into an INTERVAL FACT: `Version - RegisteredAtVersion` is exactly how many messages this
        // hub handled while the request was outstanding. A hub that handled thousands cannot claim
        // it "was idle while waiting" however empty its queue happens to be at the moment it gives
        // up. See BuildTimeoutMessage.
        long RegisteredAtVersion,
        // Opt-in sub-key from the request itself (IDiagnosticKeyed) — what makes N identical-looking
        // pending callbacks legible: N distinct keys is a fan-out, one key repeated is a retry loop.
        string? DiagnosticKey = null);

    /// <summary>
    /// Handler-side trail for the requests this hub TREE is awaiting (see
    /// <see cref="RequestFateLedger"/>). One instance per tree — the root creates it, every hosted
    /// hub inherits its parent's — so the hub that registered a callback and the hub that received
    /// (or dropped, or handled) the corresponding delivery write to the SAME ledger. Without that,
    /// a quiescing timeout can only ever report the caller's side.
    /// </summary>
    private readonly RequestFateLedger requestFates;

    /// <summary>
    /// The tree's <see cref="RequestFateLedger"/>, so this hub's <see cref="MessageService"/> can
    /// record the receiving-side stages of a delivery.
    /// </summary>
    internal RequestFateLedger RequestFates => requestFates;

    /// <summary>
    /// Records one stage on the handler-side trail of an awaited request — the seam a HANDLER uses
    /// to report on work it detached from the rule chain.
    ///
    /// <para>Needed because the pipeline's own <c>HANDLER_ENTER</c>/<c>HANDLER_EXIT</c> stages only
    /// bound the RULE CHAIN. The canonical mesh handlers return <c>request.Processed()</c>
    /// immediately and owe their reply from a composed observable they subscribed and let run — so
    /// from the pipeline's point of view a handler that answers in 3 s, one that faults silently and
    /// one that completes EMPTY without ever posting are indistinguishable. Whichever of those it is
    /// is precisely the open question on issue #981, and only the handler can answer it.</para>
    ///
    /// <para>No-ops when nothing is awaiting <paramref name="requestId"/>, so it is free to call
    /// unconditionally from a hot handler path.</para>
    /// </summary>
    /// <param name="requestId">The awaited request delivery's id (<c>request.Id</c> on the handler side).</param>
    /// <param name="stage">The stage description to append.</param>
    internal void NoteRequestStage(string? requestId, string stage)
        => requestFates.Find(requestId)?.Add(stage, Address);

    private readonly ILogger logger;
    /// <summary>The immutable configuration this hub was built from.</summary>
    public MessageHubConfiguration Configuration { get; }

    /// <summary>
    /// Fallback-hub NACK policy (<see cref="UnhandledMessageNack"/>), resolved once
    /// at construction — null on regular hubs, set on hubs standing in for a node
    /// whose NodeType produced no usable configuration. See FinishDelivery.
    /// </summary>
    private readonly UnhandledMessageNack? unhandledNack;
    private readonly HostedHubsCollection hostedHubs;
    private readonly AccessService accessService;

    /// <summary>
    /// Monotonic counter, incremented once per message processed; used for ordering and disposal
    /// sequencing.
    ///
    /// <para>🚨 <b>Interlocked, because this is now read OFF the turn (MeshWeaver#1174).</b> It is
    /// written on the hub's own turn thread and was a plain auto-property, which is correct only
    /// while every reader is that same thread. <see cref="BuildTimeoutMessage"/> reads it from the
    /// Rx timeout scheduler to report how many messages this hub handled while a request was
    /// outstanding, and a plain read there establishes no visibility with the writing turn — it
    /// could observe a stale value and report a busy hub as idle, which is the exact
    /// misdiagnosis that measurement exists to remove. <see cref="Interlocked"/> on the
    /// increment, the registration snapshot and the read makes the number mean what it says,
    /// for a counter already on a once-per-message path.</para>
    /// </summary>
    public long Version => Interlocked.Read(ref version);
    private long version;





    /// <summary>
    /// 🚨 <b>The ONE claim on this hub's teardown cause — FIRST CAUSE WINS, atomically.</b>
    /// <c>0</c> until somebody records why this hub is going down; <c>1</c> forever after.
    ///
    /// <para>Two writers race for it and they run on different threads: <see cref="HandleDispose"/>
    /// on the action block, and <see cref="NoteCascadeFrom"/> on whatever thread the owning
    /// <c>HostedHubsCollection</c> is disposing from. <see cref="NoteCascadeFrom"/> always had a
    /// first-cause-wins rule, but it was a read-then-write over two volatile fields — which is a
    /// rule, not a guarantee — and <see cref="HandleDispose"/> had none at all: it wrote its
    /// attribution unconditionally, so a routed <see cref="DisposeRequest"/> whose turn ran AFTER a
    /// direct <c>Dispose()</c> had already begun the teardown overwrote the truthful
    /// <see cref="DirectDisposeSource"/> reading with its own.</para>
    ///
    /// <para>That mattered little while the attribution appeared only on <c>[QUIESCE-START]</c>.
    /// It matters now that it rides the <c>[DISPOSE-DISCARD]</c> Error and the NACK a stranded
    /// sender receives (<a href="https://github.com/Systemorph/MeshWeaver/issues/3712">#3712</a>):
    /// a report that names the WRONG cause is worse than one that names none — it is the defect
    /// that issue was filed on, pointing somewhere else.</para>
    /// </summary>
    /// <summary>
    /// WHO, WHY and (for a cascade) the owner, as ONE reference — so a reader sees either nothing
    /// claimed or a fully-formed cause, never a half-written one.
    ///
    /// <para>🚨 This replaced a claim FLAG plus separate fields, and the difference is a real race
    /// (#4888 review). The CAS made the <i>claim</i> atomic and published nothing: a second
    /// disposer could observe the claim taken, return without writing, and have its
    /// <c>Dispose()</c> reach <c>HandleShutdownCore</c> while the winner's fields were still null —
    /// rendering the teardown as <see cref="DirectDisposeSource"/>, "nobody asked", exactly when
    /// somebody had. The window is small and its outcome is indistinguishable from the bug the
    /// attribution exists to remove, which is the worst pairing for ever noticing it.</para>
    /// </summary>
    /// <param name="RequestedBy">WHO, for a routed or direct teardown; <c>null</c> for a cascade.</param>
    /// <param name="Reason">WHY. For a cascade this is the ORIGINATING cause, passed to children
    /// unchanged so the chain names the event that started it however deep the tree is.</param>
    /// <param name="CascadeOwner">The owner whose teardown is taking this hub with it, or
    /// <c>null</c> when this hub is the subject rather than a casualty.</param>
    private sealed record TeardownCause(string? RequestedBy, string? Reason, string? CascadeOwner);

    private TeardownCause? teardownCause;

    /// <summary>
    /// Claims the right to record this hub's teardown cause. Exactly one caller ever wins.
    /// </summary>
    /// <summary>FIRST CAUSE WINS, and the cause is complete before it is visible.</summary>
    private bool TryPublishTeardownCause(TeardownCause cause) =>
        Interlocked.CompareExchange(ref teardownCause, cause, null) is null;

    /// <summary>
    /// What <c>[QUIESCE-START]</c> prints when no routed <see cref="DisposeRequest"/> brought this
    /// hub down and no owner claimed the cascade — host teardown or a <c>using</c>. Spelled once so
    /// a log reader and a log QUERY agree on the token.
    /// </summary>
    public const string DirectDisposeSource = "a direct Dispose() (no routed DisposeRequest)";

    /// <summary>WHO — the first half of the <c>[QUIESCE-START]</c> attribution.</summary>
    private string DisposalRequestedBy =>
        teardownCause is { CascadeOwner: { } owner }
            ? $"a cascade from its owner {owner}"
            : teardownCause?.RequestedBy ?? DirectDisposeSource;

    /// <summary>
    /// WHY — the second half. Never empty: a poster that said nothing is reported as having said
    /// nothing (<see cref="DisposeRequest.ReasonNotStated"/>), which is a different statement from
    /// printing no reason at all.
    /// </summary>
    private string DisposalReason =>
        teardownCause is { CascadeOwner: not null } cascade
            ? $"the owner's own teardown — {cascade.Reason ?? DisposeRequest.ReasonNotStated}"
            : teardownCause?.Reason ?? DisposeRequest.ReasonNotStated;

    /// <summary>
    /// 🚨 WHO and WHY as ONE sentence, for the teardown reports that have room for a clause and
    /// not for two structured parameters — and for the NACK text a stranded sender reads
    /// (<a href="https://github.com/Systemorph/MeshWeaver/issues/3712">#3712</a>).
    ///
    /// <para><b>Why this exists.</b> <c>[QUIESCE-START]</c> is the only line that carried the
    /// attribution, it is <c>Information</c>, and the red-log pipeline files <c>Error</c>s. So the
    /// <c>[DISPOSE-DISCARD]</c> Error — the one that becomes an ISSUE — ended with <i>"find why
    /// this hub disposed before its deferred work could run"</i> while the hub holding that line
    /// already knew the answer and printed it on a different line, at a level the incident never
    /// captures. Measured on <c>Admin/_LogIncident/d2249f800ffc2577</c> (364 occurrences,
    /// 2026-09-08 → 2026-09-14, 13 pods): every captured discard names the message, its sender and
    /// the gates it sat behind, and NONE of them says which teardown threw it away.</para>
    ///
    /// <para>Never empty and never merely absent: a poster that stated nothing is reported as
    /// having stated nothing, and a hub nobody asked about over the bus is reported as
    /// <see cref="DirectDisposeSource"/>. Both are answers.</para>
    /// </summary>
    public string DisposalAttribution =>
        $"requested by {DisposalRequestedBy}; why: {DisposalReason}";

    /// <summary>
    /// What this hub's hosted children are told when they go down with it. A hub that is itself
    /// part of a cascade passes the ORIGIN along unchanged, so the chain names the event that
    /// started it however deep the tree is — and the string cannot grow with depth.
    /// </summary>
    internal string DisposalOriginForChildren =>
        teardownCause is { CascadeOwner: not null, Reason: { } origin }
            ? origin
            : $"{Address} was torn down by {teardownCause?.RequestedBy ?? DirectDisposeSource}; why: "
              + (teardownCause?.Reason ?? DisposeRequest.ReasonNotStated);

    /// <summary>
    /// Records that this hub is going down because <paramref name="owner"/> is (#3510). Called by
    /// the owning <see cref="HostedHubsCollection"/> immediately before it disposes this hub.
    ///
    /// <para>FIRST CAUSE WINS: a child that had already been asked to recycle by name keeps that
    /// attribution, because that request is what actually started its teardown — the owner's
    /// cascade then arrives at a hub already going down. Idempotent, and safe from any thread; the
    /// fields are only read when the Quiescing phase renders the line.</para>
    /// </summary>
    /// <param name="owner">The hub whose teardown is taking this one with it.</param>
    /// <param name="originatingCause">The originating teardown, from
    /// <see cref="DisposalOriginForChildren"/>.</param>
    internal void NoteCascadeFrom(Address owner, string originatingCause)
    {
        // FIRST CAUSE WINS, as a single atomic PUBLICATION rather than a read-then-write over
        // separate fields: the other writers (HandleDispose on the action block, and
        // NoteDirectDisposalBy on whichever thread disposes) race this one, and both a pair of
        // reads AND a claim-then-write leave a window where a reader sees no cause at all. See
        // TeardownCause.
        TryPublishTeardownCause(
            new TeardownCause(RequestedBy: null, Reason: originatingCause, CascadeOwner: owner.ToString()));
    }

    /// <summary>
    /// Records WHO tore this hub down and WHY when the teardown does NOT come over the bus (#4888).
    /// Called by the external disposer immediately before <see cref="Dispose"/>, exactly as
    /// <see cref="NoteCascadeFrom"/> is called by the owning collection.
    ///
    /// <para><b>Why it is needed.</b> A direct <c>Dispose()</c> could say nothing about itself, so
    /// every such teardown rendered as <see cref="DirectDisposeSource"/> — literally "nobody asked
    /// over the bus". That is honest but useless to a reader of a <c>[DISPOSE-DISCARD]</c>, which
    /// is the Error that becomes an ISSUE: it names the discarded message, its sender and the gates
    /// it sat behind, and then cannot say which teardown threw it away. The largest single source of
    /// direct disposes is an Orleans grain deactivation, which KNOWS its reason — it logs the reason
    /// code one line before disposing — and simply had nowhere to put it.</para>
    ///
    /// <para>FIRST CAUSE WINS, through the same claim as the cascade path: a hub already asked to
    /// recycle by name keeps that attribution, because that request is what actually started its
    /// teardown. Idempotent and safe from any thread.</para>
    /// </summary>
    /// <param name="requestedBy">WHO — a short phrase naming the disposer, e.g. the grain and its
    /// deactivation.</param>
    /// <param name="reason">WHY, or <c>null</c>/blank when the disposer genuinely has none, which
    /// renders as <see cref="DisposeRequest.ReasonNotStated"/> rather than as an empty clause.</param>
    public void NoteDirectDisposalBy(string requestedBy, string? reason)
    {
        // Normalised here for the same reason HandleDispose normalises: a blank is an UNSTATED
        // reason, not a reason that renders as nothing.
        TryPublishTeardownCause(new TeardownCause(
            RequestedBy: requestedBy,
            Reason: string.IsNullOrWhiteSpace(reason) ? null : reason,
            CascadeOwner: null));
    }

    /// <summary>
    /// Disposal-health diagnostic: how many <see cref="ShutdownRequest"/> turns this hub
    /// has handled. A healthy disposal handles exactly the three phase requests
    /// (Quiescing → DisposeHostedHubs → ShutDown). A value in the thousands is the
    /// signature of the version-match repost STORM removed from <c>HandleShutdownCore</c>
    /// (see the regression test <c>Dispose_UnderContinuousLoad_DoesNotStormShutdownRequests</c>).
    /// </summary>
    public int ShutdownTurnsHandled => shutdownTurnsHandled;
    private int shutdownTurnsHandled;

    /// <summary>
    /// Sets the initial version for the hub. Only callable during initialization
    /// before any messages are processed.
    /// </summary>
    public void SetInitialVersion(long version)
    {
        Interlocked.Exchange(ref this.version, version);
    }

    /// <summary>The hub's current lifecycle phase; advances through start, quiescing, and the disposal phases.</summary>
    public MessageHubRunLevel RunLevel
    {
        get => runLevel;
        private set
        {
            // Publish only on an actual TRANSITION. Several disposal arms assign the same terminal
            // level defensively (the Dead backstop in the finally, for one), and a subscriber should
            // see the lifecycle, not the number of times a field was written.
            if (runLevel == value)
                return;
            runLevel = value;
            runLevelChanged.OnNext(value);
            // Dead is terminal: complete the source so `.LastAsync()`, `.ToTask()` and every other
            // completion-shaped composition over it terminates instead of hanging on a hub that will
            // never emit again.
            if (value == MessageHubRunLevel.Dead)
                runLevelChanged.OnCompleted();
        }
    }

    private MessageHubRunLevel runLevel;

    /// <summary>
    /// Backing source for <see cref="RunLevelChanged"/>. A BehaviorSubject, so a late subscriber is
    /// told the level the hub is ALREADY in rather than waiting for the next transition — without
    /// that, subscribing to observe the disposal window would itself race the window (#1508).
    /// Synchronized: the phases are posted from the action block, but disposal arms and the
    /// watchdog can terminalize from other threads.
    /// </summary>
    private readonly ISubject<MessageHubRunLevel> runLevelChanged =
        Subject.Synchronize(new BehaviorSubject<MessageHubRunLevel>(MessageHubRunLevel.Starting));

    /// <inheritdoc />
    public IObservable<MessageHubRunLevel> RunLevelChanged => runLevelChanged.AsObservable();

    /// <summary>
    /// Non-null once a BuildupAction faulted during init. The hub stays <see cref="MessageHubRunLevel.Started"/>
    /// (the init gate is open, so it still REACTS to messages and can be torn down) but is in a FAILED state:
    /// every non-lifecycle request is refused with a typed <see cref="DeliveryFailure"/>
    /// (<see cref="ErrorType.Failed"/>) carrying this error. This is the "status failed" marker — mirrors
    /// <c>DataContext.InitializationError</c> (MeshWeaver.Data), lifted to the hub level. Set by
    /// <see cref="EnterInitializationFailedState"/>.
    /// </summary>
    public Exception? InitializationError { get; private set; }

    /// <summary>
    /// Upper bound on how long this hub's BuildupActions may run before init is declared FAILED
    /// rather than wedging forever behind a closed gate (see <see cref="HandleInitialize"/>).
    ///
    /// <para>🚨 It is <c>Configuration.NestedInitializationBudget</c> — rung 2 of the ladder in
    /// <see cref="HubInitializationBudget"/> — never a constant of its own. A hub the parent's
    /// initialization WAITS ON must give up strictly before the parent does, or the level that
    /// knows which action hung is torn down before it can say so (Systemorph/MeshWeaver#1122,
    /// #2886). A hub tightens its whole ladder with <c>Configuration.StartupTimeout</c>.</para>
    /// </summary>
    private TimeSpan BuildupTimeout => Configuration.NestedInitializationBudget;

    private readonly IMessageService messageService;
    /// <summary>
    /// Parent hub address captured at construction. Used in disposal logging so we
    /// don't re-resolve from <see cref="MessageHubConfiguration.ParentHub"/> on a
    /// scope that may already be disposed.
    /// </summary>
    private readonly Address? parentAddress;
    /// <summary>The hub's type registry, mapping message type names to CLR types for (de)serialization and routing.</summary>
    public ITypeRegistry TypeRegistry { get; }
    /// <summary>
    /// Transitions the hub to <see cref="MessageHubRunLevel.Started"/> (idempotent) and completes the
    /// <see cref="Started"/> task. Called by the framework once initialization gates are open.
    /// </summary>
    public void Start()
    {
        if (RunLevel < MessageHubRunLevel.Started)
        {
            RunLevel = MessageHubRunLevel.Started;
            hasStarted.TrySetResult();
        }
    }

    /// <summary>
    /// Faults the <see cref="Started"/> task with <paramref name="error"/> so dependents observing
    /// startup (e.g. data-source initialization) also fault. Called when a stream errors during init.
    /// <para>
    /// Teardown-disposal is classified as CANCELLATION, not failure: when the cause chain
    /// contains an <see cref="ObjectDisposedException"/> (the shape <c>CancelCallbacks</c>
    /// pushes into pending <c>Observe</c> subjects at hub disposal — "Hub … was disposed
    /// before the response arrived"), startup didn't fail, it will simply never happen.
    /// A sync hub's <see cref="Started"/> task has NO awaiter at teardown, so faulting it
    /// armed a <c>TaskScheduler.UnobservedTaskException</c> that detonated at the next GC —
    /// xUnit v3 escalates that to a "Catastrophic failure" poisoning the next test class
    /// (the #228 capture). A canceled task never raises UnobservedTaskException, and a live
    /// awaiter still gets a graceful, typed <see cref="TaskCanceledException"/>
    /// (<c>DataContext.OpenInitializationGate</c> handles <c>IsCanceled</c> explicitly).
    /// Real startup errors keep faulting <see cref="Started"/> so dependents observe them.
    /// Pinned by <c>FailStartupTeardownClassificationTest</c> and
    /// <c>TeardownPendingSubscribeGracefulTest</c>.
    /// </para>
    /// </summary>
    /// <param name="error">The exception that caused startup to fail.</param>
    public void FailStartup(Exception error)
    {
        if (IsTeardownDisposal(error))
            hasStarted.TrySetCanceled();
        else
            hasStarted.TrySetException(error);
    }

    /// <summary>
    /// True when <paramref name="error"/> (or any exception in its cause chain) is an
    /// <see cref="ObjectDisposedException"/> — the benign teardown shape. Walks the chain
    /// because the disposal error arrives both bare (the SubscribeRequest observe path) and
    /// wrapped (e.g. <c>InvalidOperationException</c> → ODE from the DataChangeRequest
    /// observe path in <c>JsonSynchronizationStream</c>); mirrors
    /// <c>SynchronizationStream.IsObjectDisposed</c>.
    /// </summary>
    private static bool IsTeardownDisposal(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
            if (e is ObjectDisposedException)
                return true;
        return false;
    }

    /// <summary>
    /// Cancels the currently-executing handler's cancellation token and rolls a fresh one for
    /// subsequent messages, aborting a long-running handler (e.g. streaming) without disposing the hub.
    /// </summary>
    public void CancelCurrentExecution()
    {
        messageService.CancelExecution();
    }

    /// <summary>
    /// The hub's message-storm circuit-breaker (when backed by the default
    /// <see cref="MessageService"/>; <c>null</c> for an alternative message service).
    /// Exposed so the framework's tests can observe its trip signal deterministically.
    /// </summary>
    public MessageStormBreaker? StormBreaker =>
        messageService is MessageService ms ? ms.StormBreaker : null;

    /// <summary>
    /// Starts message processing and posts the initialization request.
    /// Called from Build() after SyncBuildupActions complete.
    /// </summary>
    internal void StartMessageProcessing()
    {
        messageProcessingStarted = true;
        messageService.Start();
        InstallStaleCallbackScanner();
        if (!Configuration.DeferredInitialization)
            Post(new InitializeHubRequest());
    }

    /// <summary>Periodic interval for the stale-callback scanner.</summary>
    internal static readonly TimeSpan StaleCallbackScanInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A pending callback older than this is logged at Warning. Calibrated to
    /// catch hangs that the user perceives (10–30 s is "the UI is stuck") while
    /// staying above legitimate slow request paths (cold-start hub activation,
    /// first-token agent latency, large query fan-out). Adjust via
    /// <c>MESHWEAVER_STALE_CALLBACK_MS</c> env var if a deployment has higher
    /// expected latencies.
    /// </summary>
    internal static readonly TimeSpan StaleCallbackThreshold =
        long.TryParse(Environment.GetEnvironmentVariable("MESHWEAVER_STALE_CALLBACK_MS"), out var ms)
            && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(30);

    private IDisposable? staleCallbackScannerSub;

    /// <summary>
    /// Always-on per-hub scanner. Every <see cref="StaleCallbackScanInterval"/>
    /// snapshots <see cref="SnapshotPendingCallbacks"/>, filters entries older
    /// than <see cref="StaleCallbackThreshold"/>, and logs them at Warning so
    /// hangs are observable while in flight (no need to wait for the dispose
    /// quiesce timeout to surface "we were stuck on X").
    ///
    /// <para>Cost: one timer tick per hub every 5 s + one dictionary scan;
    /// negligible. Disposed explicitly at the start of the Quiescing phase
    /// (and as a fallback via the <c>disposables</c> composite).</para>
    /// </summary>
    private void InstallStaleCallbackScanner()
    {
        var thresholdMs = (long)StaleCallbackThreshold.TotalMilliseconds;
        // 🚨 WEAK self-reference. The scanner is a DIAGNOSTIC — it must NEVER keep its
        // hub alive. Observable.Interval lives on the global Rx scheduler's TimerQueue
        // (a GC strong-root); a closure capturing `this` STRONGLY pins the hub via
        // TimerQueue → Rx PeriodicTimer → DisplayClass → MessageHub for as long as the
        // timer runs. That is fine for a hub that gets DISPOSED (the dispose path kills
        // the timer), but an ABANDONED hub — created, RunLevel=1, never disposed
        // (e.g. a sync/ SynchronizationStream hub orphaned at mesh teardown) — never
        // disposes its scanner, so the timer pins it forever and it accumulates across
        // meshes. That is the MeshHub_IsCollected leak (ClrMD: TimerQueue → Rx
        // PeriodicTimer → DisplayClass44 → MessageHub[sync/…, RunLevel=1]). With a weak
        // ref the abandoned hub is collectable; the scanner observes it dead on the next
        // tick and self-disposes. A LIVE hub is held by its real owners (parent
        // hosted-hubs / DI), so the weak ref always resolves while the hub matters.
        var weakSelf = new WeakReference<MessageHub>(this);
        var sub = new System.Reactive.Disposables.SingleAssignmentDisposable();
        sub.Disposable = Observable
            .Interval(StaleCallbackScanInterval)
            .Subscribe(_ =>
            {
                if (!weakSelf.TryGetTarget(out var self))
                {
                    // Hub was collected (abandoned + GC'd). Stop the timer so the whole
                    // scanner graph becomes unreachable. `sub` is captured directly, so
                    // this needs no reference back to the (now-gone) hub.
                    sub.Dispose();
                    return;
                }
                self.ScanStaleCallbacks(thresholdMs);
            });
        staleCallbackScannerSub = sub;
        // Also register in the disposables composite so a NORMAL teardown kills the
        // timer promptly (rather than waiting for the next tick after GC). Double-dispose
        // with the explicit Quiescing-phase dispose is harmless (Rx is idempotent).
        disposables.Add(GuardRegistrant(sub));
    }

    /// <summary>Single scan tick — extracted so the timer closure captures only a
    /// <see cref="WeakReference{T}"/> to the hub, never <c>this</c> (see
    /// <see cref="InstallStaleCallbackScanner"/>).</summary>
    private void ScanStaleCallbacks(long thresholdMs)
    {
        try
        {
            var pending = SnapshotPendingCallbacks();
            if (pending.Length == 0) return;
            var stale = pending.Where(p => p.AgeMs > thresholdMs).ToArray();
            if (stale.Length == 0) return;
            // Handler-side trail on the LIVE-mesh path too (#981). The teardown capture only ever
            // sees a stall that survived to disposal; this one fires while the mesh is still
            // serving, which is where the #981 callbacks actually go unanswered (measured ~3.1 s
            // before Dispose() was even invoked). Dial MESHWEAVER_STALE_CALLBACK_MS down on a
            // repro run and this line, not the teardown one, names the handler side first.
            // The pool's numbers ride on the line the host already writes (MeshWeaver#2543): a wall of
            // stale callbacks with a large pending-work count and a thread count pinned at the minimum
            // is a STARVED pool — timers (a flush bound, an activation budget) do not fire on it — and
            // that reading is impossible from the callbacks alone.
            TryLog(LogLevel.Warning,
                "[STALE-CALLBACK] {Address}: {Count} callback(s) pending > {ThresholdMs}ms: {Detail}{Fates} "
                + "[pool threads={PoolThreads} pendingWork={PoolPending} completed={PoolCompleted}]",
                Address, stale.Length, thresholdMs, FormatPendingCallbacks(stale),
                FormatPendingCallbackFates(stale),
                System.Threading.ThreadPool.ThreadCount, System.Threading.ThreadPool.PendingWorkItemCount,
                System.Threading.ThreadPool.CompletedWorkItemCount);
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Debug,
                "[STALE-CALLBACK] {Address}: scan tick failed: {Error}",
                Address, ex.Message);
        }
    }

    private readonly ThreadSafeLinkedList<AsyncDelivery> rules = new();
    private readonly Lock messageHandlerRegistrationLock = new();
    private readonly Lock typeRegistryLock = new();
    /// <summary>
    /// Constructs the hub: wires DI, the type registry, the message service, JSON options, the
    /// built-in lifecycle handlers (dispose / shutdown / ping / initialize) and the configured
    /// message handlers. Message processing is started separately (by the configuration's Build,
    /// after synchronous buildup completes), not from this constructor.
    /// </summary>
    /// <param name="serviceProvider">The DI service provider scoped to this hub.</param>
    /// <param name="hostedHubs">The collection that owns this hub's hosted child hubs.</param>
    /// <param name="configuration">The configuration describing address, handlers, buildup/dispose actions and timeouts.</param>
    /// <param name="parentHub">The parent hub, or <c>null</c> for a root hub; used for routing and inherited JSON options.</param>
    public MessageHub(
        IServiceProvider serviceProvider,
        HostedHubsCollection hostedHubs,
        MessageHubConfiguration configuration,
        IMessageHub? parentHub
    )
    {
        serviceProvider.Buildup(this);
        serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(GetType());
        logger = serviceProvider.GetRequiredService<ILogger<MessageHub>>();

        logger.LogDebug("Starting MessageHub construction for address {Address} with parent {Parent}", configuration.Address, parentHub?.Address);

        TypeRegistry = serviceProvider.GetRequiredService<ITypeRegistry>();
        InitializeTypes(this);

        this.hostedHubs = hostedHubs;
        // 🚨 #3510: a child torn down with its owner must be able to say so. Installed here, at
        // construction, because the collection disposes children a phase AFTER this hub's own
        // attribution is settled — a value captured now would be the empty one.
        hostedHubs.OwnerDisposalCause = () => DisposalOriginForChildren;
        ServiceProvider = serviceProvider;
        Configuration = configuration;
        unhandledNack = configuration.Get<UnhandledMessageNack>();
        parentAddress = parentHub?.Address;
        accessService = serviceProvider.GetRequiredService<AccessService>();

        // One ledger per hub TREE. Inheriting the parent's instance is what lets the RECEIVING
        // hub's stages land on the trail the REQUESTING hub reads at its quiescing timeout; a
        // per-hub ledger would reproduce the caller-only blindness this exists to remove. A root
        // hub (no parent) starts a fresh one, so the ledger's lifetime is the tree's.
        requestFates = (parentHub as MessageHub)?.requestFates ?? new RequestFateLedger();

        messageService = new MessageService(configuration.Address,
            serviceProvider.GetRequiredService<ILogger<MessageService>>(), this, parentHub);

        foreach (var disposeAction in configuration.DisposeActions)
            RegisterForDisposal(disposeAction);

        JsonSerializerOptions = this.CreateJsonSerializationOptions(parentHub);

        TypeRegistry.WithType(typeof(PingRequest), nameof(PingRequest));
        TypeRegistry.WithType(typeof(PingResponse), nameof(PingResponse));
        Register<DisposeRequest>(HandleDispose);
        // Disposal is fully SYNCHRONOUS + reactive — the handler returns immediately, kicks off
        // the Quiescing poll / hosted-hub drain as Rx subscriptions (Observable.Interval/Timer,
        // off the action block), and completes via the `disposalCompleted` ReplaySubject. No
        // async leaf, no DeliveryObservable.Run, no FromAsync on the disposal path.
        Register<ShutdownRequest>((request, _) => Observable.Return(HandleShutdownCore(request)));
        Register<PingRequest>(HandlePingRequest);
        // HandleInitialize already returns IObservable (buildup composed via Observable.Concat,
        // no await) — register it directly now that the rule chain is reactive.
        Register<InitializeHubRequest>(HandleInitialize);
        lock (messageHandlerRegistrationLock)
        {
            foreach (var messageHandler in configuration.MessageHandlers)
                Register(
                    messageHandler.MessageType,
                    (d, c) => messageHandler.AsyncDelivery.Invoke(this, d, c)
                );
        }
        Register(ExecuteRequest);
        Register(HandleCallbacks);

        // Note: messageService.Start() is called from MessageHubConfiguration.Build()
        // AFTER SyncBuildupActions complete, to ensure services like Workspace/DataContext
        // are fully configured before any messages arrive
    }

    private IMessageDelivery HandlePingRequest(IMessageDelivery<PingRequest> request)
    {
        Post(new PingResponse(), o => o.ResponseFor(request));
        return request.Processed();
    }

    /// <summary>
    /// Reactive hub initialization: composes the configured buildup observables in order
    /// (<see cref="Observable.Concat{TSource}(System.Collections.Generic.IEnumerable{IObservable{TSource}})"/>)
    /// and opens the Initialize gate when the composed sequence completes — no <c>await</c>/<c>for</c>-await.
    /// Each action advances on its first emission (or <c>DefaultIfEmpty</c>) via <c>Take(1)</c>, matching the
    /// previous FirstAsync-per-action semantics; the gate opens exactly once after every action has signalled.
    /// </summary>
    /// <remarks>
    /// <para><b>Robustness — a faulting BuildupAction must NOT wedge the hub.</b> A throw in init used to
    /// propagate out of the <c>Concat</c> so the <c>Select</c> that calls <see cref="OpenGate"/> never ran:
    /// the Initialize gate stayed closed forever, so EVERY later message deferred until the 30s
    /// deferral-timeout — which the user experiences as an unrecoverable hang (the prod AgenticPension
    /// agent-select wedge, 2026-06-16). The <c>.Catch</c> below mirrors
    /// <c>DataContext</c> (MeshWeaver.Data)'s per-context guard, lifted to the hub level so EVERY
    /// BuildupAction is covered: on a fault the hub enters a FAILED state (see
    /// <see cref="EnterInitializationFailedState"/>) that answers every request with a typed
    /// <see cref="DeliveryFailure"/> carrying the init error — FAST, not a 30s wedge — and ALWAYS opens the
    /// gate so those rejections actually flow. The error is now observable end-to-end: callers get a
    /// <c>DeliveryFailure</c> and the GUI's area binding renders it instead of spinning forever. See
    /// <c>Doc/Architecture/HubInitializationFailure.md</c>.</para>
    /// Bridged to the Task-based rule chain at the <c>Register</c> edge.
    /// </remarks>
    private IObservable<IMessageDelivery> HandleInitialize(
        IMessageDelivery<InitializeHubRequest> request, CancellationToken ct)
    {
        logger.LogDebug("Message hub {address} initializing via InitializeHubRequest", Address);

        var actions = Configuration.BuildupActions;
        logger.LogDebug("Message hub {address} has {count} BuildupActions to run", Address, actions.Count);

        // 🚨 A BuildupAction never STARTS after teardown has begun. The init turn is queued at Build and
        // runs whenever the action block reaches it — which can be after this hub's own Dispose() (a
        // transient probe is created and disposed in one breath: ContentTypeRegistration.ProbeRegister,
        // the schema probes) or after an ancestor's cascade froze the subtree. Every action is a piece
        // of the per-node control plane — a watcher over the own node, an eagerly created child hub, a
        // ticker — and installed on a hub that is already leaving each one is born dead and faults on
        // the way out: HostedHubsCollection refuses the child with a Warning ("Rejecting hosted hub
        // creation … during disposal"), the teardown errors the watcher's stream. The .Catch below
        // already classifies "teardown ENDED an action" as a recognised shutdown; this is the same
        // policy one step earlier, at the action boundary: once IsShuttingDown, the remaining actions
        // are skipped, the gate still opens (so the disposal state machine flows) and nothing is
        // installed. Measured: the boot-time content-type registration probe of the Thread NodeType
        // reached its _Exec child creation after its own Dispose() in 159 of 643 test logs of one CI
        // run (MeshWeaver CD 33619142646) — a Warning each time, and the one test that asserts a
        // fault-free probe teardown (ProbeHubCostTest) red whenever the late creation landed in its
        // window. Pinned by InitializationStopsAtTeardownStartTest.
        // 🚨 The bring-up OBSERVES its cancellation. `ct` is the execution token Dispose() cancels
        // for a hub that never reached Started (see Dispose): a BuildupAction parked on a source
        // that never emits — a data source's initial load that never answers, a NodeType that never
        // compiles — used to hold this turn for the whole StartupTimeout (120 s) with the
        // ShutdownRequest queued behind it, so an owner disposing such a child waited on it too.
        // Racing the buildup against the token ends the turn the moment the hub is told to go
        // down: the cancellation errors into the Catch below, whose shutdown arm records no
        // failure. A ShutdownRequest runs with CancellationToken.None; every other turn's token is
        // the stall detector's, so nothing else changes here.
        var cancelled = Observable.Create<IList<Unit>>(observer =>
            ct.Register(() => observer.OnError(new OperationCanceledException(
                $"Hub {Address} was told to shut down before its initialization completed", ct))));

        var skipLogged = false;
        // 🚨 Which action the sequential Concat is ON, so the timeout below can NAME what did not
        // finish (issue #2886). Semantics: the index of the LAST action the Concat subscribed to
        // before the bound fired; -1 while none has been. Written on whichever thread delivered
        // the previous action's completion, read on the Timeout's scheduler thread — so the write
        // is an Interlocked exchange and the read a Volatile read: a FENCE, never a gate (a lock in
        // a hub parks the action block, and a Subject would be a channel for one integer). The
        // only ambiguity is a bound firing inside the transition from action i to i+1, microseconds
        // wide, where either reading names an action adjacent to where the Concat stood when it was
        // disposed; the timeout tears the whole chain down either way.
        var pendingAction = -1;
        return Observable
            .Concat(actions.Select((a, index) => Observable.Defer(() =>
            {
                Interlocked.Exchange(ref pendingAction, index);
                if (!IsShuttingDown)
                    return a(this).DefaultIfEmpty(Unit.Default).Take(1);
                if (!skipLogged)
                {
                    skipLogged = true;
                    logger.LogDebug(
                        "Message hub {address} began shutting down before its BuildupActions completed — the remaining actions are skipped; nothing is installed on a hub that is leaving",
                        Address);
                }
                return Observable.Empty<Unit>();
            })))
            .ToList()
            .Amb(cancelled)
            // 🚫 Liveness bound. A BuildupAction that HANGS — never emits and never completes (a
            // dependency that never initialises, a stuck NodeType compile, a subscribe that never
            // fires) — leaves the Concat incomplete, so the gate never opens and EVERY message defers
            // to the 30s deferral-timeout: the hub wedges forever. A throw is caught below; a hang
            // raises no exception, so convert "never completes within the budget" into a
            // TimeoutException the SAME .Catch handles. Generous default (every legit init, incl. a
            // NodeType compile, finishes well inside it); a hub may tighten it via
            // Configuration.StartupTimeout, and every hub born inside THIS one's initialization
            // contracts from there.
            .Timeout(BuildupTimeout)
            .Select(_ =>
            {
                logger.LogDebug("Message hub {address} BuildupActions complete, opening Initialize gate", Address);

                // Open the Initialize gate - this will set RunLevel to Started if all other gates are also open
                OpenGate(MessageHubConfiguration.InitializeGateName);

                return request.Processed();
            })
            .Catch((Exception ex) =>
            {
                // Recognized shutdown, NOT a failure: this hub is being torn down (its own Dispose
                // began, or an ancestor's disposal froze the subtree) and the teardown is what ended
                // the BuildupAction — canonically an ObjectDisposedException from a disposing hub
                // erroring its pending response subjects into a child's in-flight init request (the
                // $model-probe → sync/{id} race, issues #1122–#1125: a transient probe hub is
                // DESIGNED to be disposed mid-life, so its children's dispose-during-init is a
                // normal path). Reporting it as "initialization failed → FAILED state" logged a
                // fail-level error and left FAILED residue on a hub that was already dying — the
                // same defect class as the CompileWatcher shutdown-as-fault reports. Terminate the
                // init cleanly instead: no error log, no FAILED marker; still open the gate so any
                // remaining lifecycle traffic (the disposal state machine) flows.
                //
                // 🚨 IsShuttingDown alone does NOT see every teardown (issue #2444). When the hub's
                // DI scope is disposed OUT-OF-BAND — the host's root container torn down on a
                // Host.StartAsync abort or pod shutdown, or an ancestor's Autofac scope disposing
                // this hub's child scope — Autofac flips the scope's disposed flag BEFORE its
                // disposer reaches the tracked hub instance (Disposable.Dispose sets the flag,
                // THEN runs Dispose(true)). A BuildupAction resolving in that window observes
                // ObjectDisposedException while IsShuttingDown is still false: no ancestor HUB
                // Dispose ran, so no CloseCreation cascade froze the subtree. That was reported
                // as "initialization failed → FAILED state" for a hub whose own Dispose was
                // moments away — an Error-level log and FAILED residue for routine teardown
                // (mesh/... data-source hubs during the 2026-08-26 host-start aborts). A disposed
                // scope means this hub cannot live regardless — its own disposal is already queued
                // in the same disposer, by construction — so classify it as the shutdown it is.
                // The classifier is the shared, probe-gated ScopeTeardown (one shape for init,
                // routing, the permission fold and the layout error path — #2444/#2638/#2679).
                if (IsShuttingDown || this.IsTerminatedByScopeTeardown(ex))
                {
                    logger.LogDebug(ex,
                        "Hub {Address} initialization ended by shutdown ({ExceptionType}) — recognized "
                        + "shutdown outcome, no failure state recorded.", Address, ex.GetType().Name);
                    OpenGate(MessageHubConfiguration.InitializeGateName);
                    return Observable.Return(request.Processed());
                }

                // 🚨 A TRANSIENT INFRASTRUCTURE fault is not a property of this activation (#4067,
                // #4068): the database was away, a name did not resolve. Latching the hub FAILED
                // for it turned a two-minute DNS blip into "this address is broken until the
                // process restarts" — every later request answered with a terminal failure, no
                // path back. A hub that demand routing re-creates is RETIRED instead: whatever is
                // parked behind its gate is answered "ask again", and the next delivery activates
                // a fresh hub whose BuildupActions run against the dependency that has come back.
                if (InfrastructureFault.IsTransient(ex) && TryRetireAfterTransientInitializationFault(ex))
                    return Observable.Return(request.Processed());

                // Init failed — a BuildupAction faulted (threw) or HUNG (TimeoutException from the bound
                // above). Do NOT leave the gate closed (→ the 30s-per-message deferral wedge): enter a
                // FAILED state that surfaces a clear DeliveryFailure for every later request, then
                // ALWAYS open the gate so those rejections (and disposal) can flow.
                // 🚨 NAME what did not finish; never guess at it (#1122, and #2886 for this line).
                // This sentence used to read "a BuildupAction did not complete within 120s (a hung
                // dependency or stuck compile)" — two candidates, neither measured, and no way to
                // tell WHICH of the hub's actions was the one still pending. Naming the pending
                // action here is the part this layer CAN say; the action's own report, if it has
                // one, is the next layer's to give when it is disposed incomplete — and that layer
                // can now GET there, because BuildupTimeout is a contracting rung rather than the
                // flat constant every nesting level used to share (HubInitializationBudget).
                var pending = Volatile.Read(ref pendingAction);
                var reason = ex is TimeoutException
                    ? $"BuildupAction {DescribeBuildupAction(actions, pending)} did not complete within "
                      + $"{BuildupTimeout.TotalSeconds:F0}s "
                      + "— the actions before it had signalled; a dependency it waits on hung, or a compile inside it never finished"
                    : $"BuildupAction {DescribeBuildupAction(actions, pending)} faulted ({ex.GetType().Name}: {ex.Message})";
                logger.LogError(ex,
                    "Hub {Address} initialization failed — {Reason}. Hub is now in FAILED state.{Recovery}",
                    Address, reason, TransientLatchNote(ex));
                EnterInitializationFailedState(new InvalidOperationException(reason, ex));
                OpenGate(MessageHubConfiguration.InitializeGateName);
                return Observable.Return(request.Failed($"Hub '{Address}' initialization failed — {reason}"));
            });
    }

    /// <summary>
    /// Names one of a hub's BuildupActions by position and by the method behind its delegate, for
    /// the initialization failure line — <c>3 of 3 (DataExtensions.StartDataSourcesAndOpenGate)</c>.
    /// A method group names itself; a lambda names the compiler's closure method, which still
    /// identifies the registration site by its enclosing method. <paramref name="index"/> is
    /// <c>-1</c> when no action was ever subscribed, which the sentence says rather than
    /// pretending an action was.
    /// </summary>
    /// <param name="actions">The hub's BuildupActions, in Concat order.</param>
    /// <param name="index">The zero-based position of the action the Concat was on, or -1.</param>
    /// <returns>A short label for the log line.</returns>
    internal static string DescribeBuildupAction(
        ImmutableList<Func<IMessageHub, IObservable<Unit>>> actions, int index)
    {
        if (index < 0 || index >= actions.Count)
            return $"(none of {actions.Count} started)";
        var method = actions[index].Method;
        var owner = method.DeclaringType?.Name;
        var name = owner is null ? method.Name : $"{owner}.{method.Name}";
        return $"{index + 1} of {actions.Count} ({name})";
    }

    /// <summary>
    /// The sentence appended to a FAILED-state log line when the cause was a transient
    /// infrastructure fault on a hub that CANNOT be retired (no
    /// <see cref="MessageHubConfiguration.WithReactivationOnDemand"/>): the latch is the honest
    /// answer for it, and the reader must know a restart, not a fix, recovers it. Empty otherwise.
    /// </summary>
    private static string TransientLatchNote(Exception ex)
        => InfrastructureFault.IsTransient(ex)
            ? " The cause is a TRANSIENT infrastructure fault, but this hub is not re-created on demand "
              + "(no WithReactivationOnDemand), so it stays FAILED until it is recycled or the process restarts."
            : string.Empty;

    /// <summary>
    /// Retires this activation after its initialization met a transient infrastructure fault —
    /// when, and only when, demand routing will re-create it
    /// (<see cref="MessageHubConfiguration.WithReactivationOnDemand"/>).
    ///
    /// <para>🚨 Order matters and is deliberate — and it is the OPPOSITE of what it was (issue
    /// #4261). <see cref="FailGate(string,string,ErrorType)"/> comes FIRST, carrying both the
    /// SPECIFIC reason and its classification, so the backlog is answered before any teardown
    /// exists that could answer it differently; <see cref="Dispose"/> follows. It used to be the
    /// other way round, to make the drain-time read of <see cref="IsShuttingDown"/> come out
    /// transient — but <c>Dispose()</c> only POSTS the <c>ShutdownRequest</c> and its drain runs
    /// later on the action block, so the two ends raced for the same deferred backlog and the
    /// requester was told "the message was never processed" instead of naming the database.
    /// Stating the <see cref="ErrorType"/> removes the reason the ordering existed, and with it the
    /// race. Nothing is recorded in <see cref="InitializationError"/>: this activation is going
    /// away, and the FAILED marker exists to describe one that stays.</para>
    /// </summary>
    /// <param name="ex">The transient fault the initialization met.</param>
    /// <returns><c>true</c> when the hub was retired; <c>false</c> when it is not re-created on
    /// demand and must take the FAILED latch instead.</returns>
    private bool TryRetireAfterTransientInitializationFault(Exception ex)
    {
        if (!Configuration.ReactivatesOnDemand || Address.Type == AddressExtensions.MeshType)
            return false;
        var reason = ShutdownNack.RetryForTheAuthoritativeAnswer(
            Address,
            $"RunLevel={RunLevel}, {ShutdownNack.FormatActivationTag(this)}",
            "its initialization met a transient infrastructure fault "
            + $"({ex.GetType().Name}: {ex.Message}) and this activation is retired");
        logger.LogWarning(ex,
            "Hub {Address} initialization met a transient infrastructure fault — retiring this activation "
            + "instead of latching it FAILED; the address reactivates on the next delivery and initializes "
            + "again. {Reason}", Address, reason);
        FailGate(MessageHubConfiguration.InitializeGateName, reason, ErrorType.ShuttingDown);
        Dispose();
        return true;
    }

    /// <summary>
    /// Puts the hub in a FAILED state after an initialization fault. Registers a front-of-chain rule that
    /// answers every subsequent request with a <see cref="DeliveryFailure"/> carrying the init error, so
    /// callers — and the GUI's area binding — get a clear, FAST error instead of the 30s deferral-timeout
    /// wedge a closed init gate produces. Lifecycle/control messages pass through unchanged so the hub can
    /// still be torn down and the failure can't ping-pong (the same bypass set
    /// <see cref="MessageService"/> applies at the gate). Mirrors
    /// <c>DataContext</c> (MeshWeaver.Data)'s per-context guard, lifted to the hub level.
    /// </summary>
    private void EnterInitializationFailedState(Exception initException)
    {
        // Status = failed. RunLevel stays Started (the gate opens below) so the hub still reacts to and
        // refuses messages; InitializationError is the queryable "failed" marker.
        InitializationError = initException;
        var errorMessage = $"Hub '{Address}' initialization failed: {initException.Message}";
        Register(delivery =>
        {
            // Let lifecycle/control traffic through: disposal must still work, and a DeliveryFailure must
            // never beget another DeliveryFailure (storm). Everything else is rejected with the init error.
            if (delivery.Message is DeliveryFailure or ShutdownRequest or DisposeRequest
                or InitializeHubRequest or HeartBeatEvent)
                return delivery;

            logger.LogWarning("Hub {Address} is in FAILED state. Rejecting {MessageType} from {Sender}: {Error}",
                Address, delivery.Message.GetType().Name, delivery.Sender, errorMessage);
            Post(new DeliveryFailure(delivery) { ErrorType = ErrorType.Failed, Message = errorMessage },
                o => o.ResponseFor(delivery));
            return delivery.Processed();
        });
    }

    #region Message Types
    private void InitializeTypes(object instance)
    {
        foreach (
            var registry in instance
                .GetType()
                .GetAllInterfaces()
                .Select(i => GetTypeAndHandler(i, instance))
                .Where(x => x != null)
        )
        {
            if (registry!.Action != null)
                Register(registry.Action, d => registry.Type.IsInstanceOfType(d.Message));


            WithTypeAndRelatedTypesFor(registry.Type);
        }
    }
    private void WithTypeAndRelatedTypesFor(Type? typeToRegister)
    {
        if (typeToRegister == null) return;

        lock (typeRegistryLock)
        {
            logger.LogTrace("Registering type {TypeName} and related types in hub {Address}", typeToRegister.Name, Address);

            TypeRegistry.WithType(typeToRegister);

            var types = typeToRegister
                .GetAllInterfaces()
                .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IRequest<>))
                .SelectMany(x => x.GetGenericArguments());

            foreach (var type in types)
            {
                TypeRegistry.WithType(type);
            }

            if (typeToRegister.IsGenericType)
            {
                foreach (var genericType in typeToRegister.GetGenericArguments())
                    TypeRegistry.WithType(genericType);
            }

            logger.LogTrace("Completed type registration for {TypeName} in hub {Address}", typeToRegister.Name, Address);
        }
    }

    private TypeAndHandler? GetTypeAndHandler(Type type, object instance)
    {
        if (
            !type.IsGenericType
            || !MessageHubPluginExtensions.HandlerTypes.Contains(type.GetGenericTypeDefinition())
        )
            return null;
        var genericArgs = type.GetGenericArguments();

        var cancellationToken = new CancellationTokenSource().Token; // todo: think how to handle this
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            return new(
                genericArgs.First(),
                CreateDelivery(genericArgs.First(), type, instance, Expression.Constant(cancellationToken))
            );
        if (type.GetGenericTypeDefinition() == typeof(IMessageHandlerAsync<>))
            return new(
                genericArgs.First(),
                CreateDelivery(
                    genericArgs.First(),
                    type,
                    instance,
                    Expression.Constant(cancellationToken)
                )
            );

        return null;
    }
    private AsyncDelivery CreateDelivery(
        Type messageType,
        Type interfaceType,
        object instance,
        Expression? cancellationToken
    )
    {
        var prm = Expression.Parameter(typeof(IMessageDelivery));
        var cancellationTokenPrm = Expression.Parameter(typeof(CancellationToken));

        var expressions = new List<Expression>
        {
            Expression.Convert(prm, typeof(IMessageDelivery<>).MakeGenericType(messageType))
        };
        if (cancellationToken != null)
            expressions.Add(cancellationToken);
        var handlerCall = Expression.Call(
            Expression.Constant(instance, interfaceType),
            interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public).First(),
            expressions
        );

        // Sync IMessageHandler<> returns IMessageDelivery → wrap in Observable.Return;
        // async IMessageHandlerAsync<> already returns IObservable<IMessageDelivery>.
        if (interfaceType.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
            handlerCall = Expression.Call(
                null,
                MessageHubPluginExtensions.ObservableReturnMethod,
                handlerCall
            );

        var lambda = Expression
            .Lambda<Func<IMessageDelivery, CancellationToken, IObservable<IMessageDelivery>>>(
                handlerCall,
                prm,
                cancellationTokenPrm
            )
            .Compile();
        return (d, c) => lambda(d, c);
    }

    private record TypeAndHandler(Type Type, AsyncDelivery? Action);



    #endregion



    private IObservable<IMessageDelivery> HandleMessageAsync(
        IMessageDelivery delivery,
        AsyncDelivery[] ruleChain,
        CancellationToken cancellationToken
    )
    {
        // Reactive fold over the rule chain: each rule maps the running delivery
        // to an IObservable that emits the transformed delivery, fed to the next
        // rule via SelectMany. Same sequential semantics as the previous
        // await-loop — sync rules (Observable.Return) collapse synchronously on
        // subscribe (on the action-block thread), genuinely-async rules complete
        // later via the pool. The chain is BUILT here (one SelectMany per rule);
        // it runs when the actor-loop edge subscribes (.ToTask).
        //
        // 🚨 ruleChain is a SNAPSHOT taken under ThreadSafeLinkedList's read lock — NOT a live
        // walk of LinkedListNode.Next. A raw .Next walk races a concurrent rules.Remove(node)
        // (a handler disposable firing during hub teardown / rapid sync-hub churn): LinkedList
        // invalidates the removed node's owning-list reference before its next pointer, so a
        // racing get_Next() dereferences list.head and throws NRE → the delivery fails → sync
        // streams see it as [SYNC_STREAM] OnError and their subscribers time out (a different
        // sync-hub test flakes each bulk run). Iterating the snapshot is immune to that race.
        if (ruleChain.Length > 500)
            throw new InvalidOperationException($"HandleMessageAsync rule count exceeded 500 in hub {Address} for {delivery.Message.GetType().Name}");
        IObservable<IMessageDelivery> result = Observable.Return(delivery);
        foreach (var rule in ruleChain)
            result = result.SelectMany(d => rule.Invoke(d, cancellationToken));
        return result;
    }

    /// <summary>
    /// Opens the named initialization gate on the message service, releasing messages deferred behind it.
    /// </summary>
    /// <param name="name">The name of the gate to open.</param>
    /// <returns><c>true</c> if the gate existed and was opened; <c>false</c> if it was already open or not found.</returns>
    public bool OpenGate(string name)
    {
        return messageService.OpenGate(name);
    }

    /// <summary>
    /// Declares the named initialization gate DEAD — see <see cref="IMessageHub.FailGate(string,string,ErrorType)"/>
    /// for the contract. Everything deferred behind it is answered immediately, and later messages
    /// that would have been deferred are answered too rather than parked.
    /// </summary>
    /// <param name="name">The name of the gate that can never open.</param>
    /// <param name="reason">Why it can never open; becomes the failure message senders receive.</param>
    /// <param name="errorType">
    /// How the refusal is classified. Stated by the caller and carried with the reason, never
    /// re-derived from this hub's run level at drain time (#4261).
    /// </param>
    /// <returns><c>true</c> if the gate existed and was failed; <c>false</c> if it was not found.</returns>
    public bool FailGate(string name, string reason, ErrorType errorType)
    {
        return messageService.FailGate(name, reason, errorType);
    }

    /// <summary>
    /// The unclassified form — the refusal's <see cref="ErrorType"/> is left to be derived from
    /// this hub's run level at drain time. Prefer the three-argument overload: a classification
    /// derived from teardown progress is a race, which is what #4261 removed.
    /// </summary>
    /// <param name="name">The name of the gate that can never open.</param>
    /// <param name="reason">Why it can never open; becomes the failure message senders receive.</param>
    /// <returns><c>true</c> if the gate existed and was failed; <c>false</c> if it was not found.</returns>
    public bool FailGate(string name, string reason)
    {
        return messageService.FailGate(name, reason, ErrorType.Unknown);
    }


    /// <summary>
    /// Threshold above which per-message dispatch latency is reported at
    /// <see cref="LogLevel.Information"/> so it surfaces in Grafana/Loki without
    /// LogLevel.Trace flooding. Tuned so chat / layout / routing hops only log
    /// when something is genuinely slow.
    /// </summary>
    // 🚨 In STOPWATCH ticks, because it is compared against `Stopwatch.GetTimestamp()` deltas
    // (below). It used to be `TimeSpan.TicksPerMillisecond * 500` — 100-ns ticks — which on Linux
    // (Stopwatch.Frequency = 1e9) is 5 ms, so every dispatch over 5 ms logged as SLOW: 366 lines
    // per package install, 16,000 lines per gate shard, all at Information (measured
    // 2026-09-13 on Plugins run 34731952463). The threshold the comment above describes — 500 ms
    // — is what this now is, on every platform.
    private static readonly long SlowDispatchTicks = Stopwatch.Frequency / 2;

    // Reactive end-to-end: IObservable, no async/await, no Task in the signature.
    // Runs INLINE on the turn thread (Defer → factory on Subscribe); a synchronous
    // rule chain completes inline, so the turn never leaves the thread. AccessContext
    // is set on entry and restored on terminate — in Select (success) BEFORE
    // FinishDelivery and in Catch (error) — the reactive equivalent of the old
    // try/finally restore.
    IObservable<IMessageDelivery> IMessageHub.HandleMessageAsync(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    ) => Observable.Defer(() =>
    {
        Interlocked.Increment(ref version);
        var dispatchStartTicks = Stopwatch.GetTimestamp();

        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        string? messageTypeName = traceEnabled ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_START | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Version: {Version}",
                messageTypeName, Address, delivery.Id, Version);

        if (IsDisposing && delivery.Message is ShutdownRequest shutdownReq)
            logger.LogDebug("Processing ShutdownRequest in {Address} : RunLevel={RunLevel}, Version={RequestVersion}, Expected={ExpectedVersion}",
                Address, shutdownReq.RunLevel, shutdownReq.Version, Version - 1);

        // 🚨 Systematic AccessContext propagation — stamp the SENDER's identity for
        // the duration of handling. Only USER identities propagate to AsyncLocal;
        // hub-shaped principals MUST NOT leak. See Doc/Architecture/AccessContextPropagation.md.
        var prevContext = accessService.Context;
        if (delivery.AccessContext is not null
            && !AccessService.LooksLikeHubPrincipal(delivery.AccessContext.ObjectId))
            accessService.SetContext(delivery.AccessContext);

        // Snapshot the rule chain ONCE under the list's read lock (see HandleMessageAsync) so a
        // concurrent rules.Remove during teardown can't NRE the iteration.
        var ruleChain = rules.Snapshot();
        if (traceEnabled)
            logger.LogTrace(ruleChain.Length > 0
                    ? "MESSAGE_FLOW: HUB_PROCESSING_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId}"
                    : "MESSAGE_FLOW: HUB_NO_RULES | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        var chain = ruleChain.Length > 0
            ? HandleMessageAsync(delivery, ruleChain, cancellationToken)
            : Observable.Return(delivery);

        return chain
            .Select(handled =>
            {
                accessService.SetContext(prevContext);
                var result = FinishDelivery(handled);
                if (traceEnabled)
                    logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_END | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Result: {State}",
                        messageTypeName, Address, delivery.Id, result.State);
                var elapsedTicks = Stopwatch.GetTimestamp() - dispatchStartTicks;
                if (elapsedTicks > SlowDispatchTicks)
                {
                    var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    logger.LogInformation(
                        "MESSAGE_FLOW: SLOW_DISPATCH | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Elapsed: {ElapsedMs:F0}ms | Sender: {Sender} | Target: {Target}",
                        messageTypeName ?? delivery.Message.GetType().Name,
                        Address, delivery.Id, elapsedMs, delivery.Sender, delivery.Target);
                }
                return result;
            })
            .Catch((Exception ex) =>
            {
                accessService.SetContext(prevContext);
                return Observable.Throw<IMessageDelivery>(ex);
            });
    });

    private IMessageDelivery FinishDelivery(IMessageDelivery delivery)
    {
        // Per-message hot path. Skip the GetType().Name + boxing when Debug is off.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("FinishDelivery called for {MessageType} (ID: {MessageId}) with state {State} in {Address}",
                delivery.Message.GetType().Name, delivery.Id, delivery.State, Address);

        if (delivery.State == MessageDeliveryState.Submitted)
        {
            // Fallback-hub contract (UnhandledMessageNack policy): this hub stands in
            // for a node whose NodeType couldn't produce a real configuration. Anything
            // its (default/overlay) config didn't handle — including RawJson deliveries
            // whose type the broken assembly would have registered — is answered with a
            // typed DeliveryFailure naming the broken NodeType, never silently Ignored.
            // Guard: never NACK a NACK (DeliveryFailure ping-pong).
            var nackPolicy = unhandledNack;
            if (nackPolicy is not null)
            {
                if (delivery.Message is DeliveryFailure)
                    return delivery.Ignored();

                logger.LogWarning(
                    "Unhandled {MessageType} (ID: {MessageId}) in fallback hub {Address} - answering {ErrorType} NACK: {Reason}",
                    delivery.Message.GetType().Name, delivery.Id, Address, nackPolicy.ErrorType, nackPolicy.Reason);
                var nackPosted = false;
                try
                {
                    Post(new DeliveryFailure(delivery)
                    {
                        ErrorType = nackPolicy.ErrorType,
                        NodeTypePath = nackPolicy.NodeTypePath,
                        Message = nackPolicy.Reason
                    }, o => o.ResponseFor(delivery));
                    nackPosted = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post fallback NACK for {MessageType} (ID: {MessageId}) in {Address}",
                        delivery.Message.GetType().Name, delivery.Id, Address);
                }
                // 🚨 Mark ONLY when the typed NACK actually went out — see
                // MessageService.FailureAlreadyReported. Marking after a THROWN post would suppress
                // the follow-up and leave the caller with NO failure response, which is the silent
                // park this NACK path exists to prevent.
                var failed = delivery.Failed(nackPolicy.Reason);
                return nackPosted
                    ? failed.WithProperty(MessageService.FailureAlreadyReported, true)
                    : failed;
            }

            // Check if this is a request that expects a response
            var messageType = delivery.Message.GetType();
            var isRequest = messageType.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

            logger.LogDebug("Message {MessageType} (ID: {MessageId}) is request: {IsRequest} in {Address}",
                messageType.Name, delivery.Id, isRequest, Address);

            if (isRequest)
            {
                // Send DeliveryFailure response for unhandled requests
                var failure = DeliveryFailure.FromException(delivery,
                    new InvalidOperationException($"No handler found for message type {messageType.Name}"));
                failure = failure with { ErrorType = ErrorType.NotFound };

                logger.LogWarning("No handler found for request {MessageType} (ID: {MessageId}) in {Address} - sending DeliveryFailure response",
                    messageType.Name, delivery.Id, Address);

                try
                {
                    Post(failure, o => o.ResponseFor(delivery));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post DeliveryFailure message for unhandled request {MessageType} (ID: {MessageId}) in {Address}",
                        messageType.Name, delivery.Id, Address);
                }

                return delivery.Failed($"No handler found for {messageType.Name}");
            }

            return delivery.Ignored();
        }
        return delivery;
    }

    private readonly TaskCompletionSource hasStarted = new();
    /// <summary>
    /// Completes when the hub has finished initialization; faults via <see cref="FailStartup"/> if startup failed.
    /// </summary>
    public Task Started => hasStarted.Task;








    /// <summary>
    /// Sync factory that returns an <see cref="IObservable{IMessageDelivery}"/> for the
    /// response to <paramref name="delivery"/>. The observable emits exactly one item
    /// when the response arrives (or <c>OnError</c> for <see cref="DeliveryFailureException"/> /
    /// <see cref="TimeoutException"/>). No Task, no <c>TaskCompletionSource</c>, no
    /// <c>async</c>: just an <see cref="System.Reactive.Subjects.AsyncSubject{T}"/> whose
    /// emission is triggered when <see cref="HandleCallbacks"/> matches the response.
    /// </summary>
    public IObservable<IMessageDelivery> Observe(IMessageDelivery delivery)
    {
        var requestType = delivery.Message?.GetType().Name ?? "<null>";
        var observable = ObserveById(delivery.Id, requestType, delivery.Target);
        // This overload registers the callback for a delivery the caller ALREADY posted, so every
        // stage that happened before now was missed. Mark it, or a trail carrying only the
        // registration would read as "nothing was ever posted" — the opposite of the truth (#981).
        requestFates.Find(delivery.Id)?.Add(
            "REGISTERED_AFTER_POST (Observe(delivery) overload — earlier stages not recorded)", Address);
        return ContinueOffBlockIfDeclared(
            RestoreUserContextOnEmission(observable, delivery.AccessContext), delivery.Message);
    }

    /// <summary>
    /// Posts <paramref name="r"/> with a pre-generated message id and returns the
    /// observable for its response. Registering the subject BEFORE posting avoids the
    /// race where a synchronously-handled response arrives before the subscription is
    /// in place.
    /// </summary>
    public IObservable<IMessageDelivery> Observe(object r, Func<PostOptions, PostOptions> options)
    {
        if (r is IMessageDelivery existing)
            return Observe(existing);

        // Capture the caller's AccessContext at observe-time. The response delivery
        // arrives on the hub action block where AsyncLocal is the receiving hub's
        // identity (impersonated). Without re-seeding here, every Subscribe callback
        // would post under the wrong identity. See AsynchronousCalls.md.
        var capturedCtx = accessService.Context;
        var messageId = Guid.NewGuid().AsString();
        // Resolve the target up-front so the pending-callback diagnostic can name
        // which hub we're waiting on. We only run `options(...)` once — Post below
        // gets the same composed PostOptions via WithMessageId chaining.
        var probeOptions = options(new PostOptions(Address));
        var requestType = r?.GetType().Name ?? "<null>";
        var subject = GetOrAddResponseSubject(messageId, requestType, probeOptions.Target,
            (r as IDiagnosticKeyed)?.DiagnosticKey);
        // 🔍 DIAGNOSTIC ONLY (#981) — records and RETHROWS; it changes no behaviour.
        //
        // The callback is registered on the line above and the delivery is posted on the line
        // below. A throw BETWEEN them leaves a pending callback that nothing will ever answer:
        // the entry is in responseSubjects, no delivery carrying its id ever reaches the pipeline,
        // and the quiescing budget reports it as a leak with no handler-side stages at all. That
        // is precisely the shape the first captures showed, so the window needs its own stage
        // rather than being inferred from an absence.
        try
        {
            Post(r, opts => options(opts).WithMessageId(messageId));
        }
        catch (Exception postEx)
        {
            requestFates.Find(messageId)?.Add($"POST_THREW {postEx.GetType().Name}: {postEx.Message}", Address);
            throw;
        }
        return ContinueOffBlockIfDeclared(
            RestoreUserContextOnEmission(
                WrapWithCancelOnDispose(
                    ApplyTimeout(subject, requestType, probeOptions.Target, messageId),
                    messageId, subject),
                capturedCtx),
            r);
    }

    /// <summary>
    /// <see cref="Observe(object, Func{PostOptions, PostOptions})"/> with a CALLER-SUPPLIED id —
    /// the #2882 seam. Identical register-subject-then-post ordering; the only difference is who
    /// mints the id, which is what lets the caller arm id-keyed state (the cross-hub write's
    /// <c>LatePatchResponseRegistry</c> entry) BEFORE anything can answer.
    /// </summary>
    public IObservable<IMessageDelivery>? Observe(object r, Func<PostOptions, PostOptions> options, string messageId)
    {
        // Same identity capture as the self-minting overload — see its comment.
        var capturedCtx = accessService.Context;
        var probeOptions = options(new PostOptions(Address));
        var requestType = r?.GetType().Name ?? "<null>";
        var subject = GetOrAddResponseSubject(messageId, requestType, probeOptions.Target,
            (r as IDiagnosticKeyed)?.DiagnosticKey);
        IMessageDelivery? posted;
        try
        {
            posted = Post(r, opts => options(opts).WithMessageId(messageId));
        }
        catch (Exception postEx)
        {
            requestFates.Find(messageId)?.Add($"POST_THREW {postEx.GetType().Name}: {postEx.Message}", Address);
            throw;
        }
        if (posted is null)
        {
            // The address could not be resolved, so nothing will ever answer this id. Remove the
            // subject registered three lines up rather than letting it ripen into a leaked
            // pending callback the quiescing budget reports, and hand the caller back its
            // existing "unresolvable address" error path via null.
            lock (responseSubjects)
            {
                if (responseSubjects.TryGetValue(messageId, out var entry)
                    && ReferenceEquals(entry.Subject, subject))
                    responseSubjects.Remove(messageId);
            }
            return null;
        }
        return ContinueOffBlockIfDeclared(
            RestoreUserContextOnEmission(
                WrapWithCancelOnDispose(
                    ApplyTimeout(subject, requestType, probeOptions.Target, messageId),
                    messageId, subject),
                capturedCtx),
            r);
    }

    private IObservable<IMessageDelivery> ObserveById(string messageId,
        string requestType = "<unknown>",
        Address? target = null)
    {
        var subject = GetOrAddResponseSubject(messageId, requestType, target);
        return WrapWithCancelOnDispose(
            ApplyTimeout(subject, requestType, target, messageId),
            messageId, subject);
    }

    /// <summary>
    /// Wraps a response observable so disposing the downstream subscription removes
    /// the pending callback entry from <see cref="responseSubjects"/>. Without this,
    /// a Subscribe disposed before the response arrives leaves the entry in the
    /// dictionary until <see cref="MessageHubConfiguration.RequestTimeout"/> expires
    /// (~30s). Test bases' quiescing-budget leak check (~0.5s) flags this as a leaked
    /// callback even though the application-level subscription is gone.
    /// </summary>
    private IObservable<IMessageDelivery> WrapWithCancelOnDispose(
        IObservable<IMessageDelivery> source,
        string messageId,
        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> subject)
    {
        return System.Reactive.Linq.Observable.Create<IMessageDelivery>(observer =>
        {
            var sub = source.Subscribe(observer);
            return new System.Reactive.Disposables.CompositeDisposable(
                sub,
                System.Reactive.Disposables.Disposable.Create(() =>
                {
                    lock (responseSubjects)
                    {
                        if (responseSubjects.TryGetValue(messageId, out var entry)
                            && ReferenceEquals(entry.Subject, subject))
                        {
                            responseSubjects.Remove(messageId);
                            // Nobody is awaiting it any more — drop the trail with the callback so
                            // the ledger stays bounded by the in-flight set.
                            requestFates.Untrack(messageId);
                        }
                    }
                }));
        });
    }

    /// <summary>
    /// Wraps a response observable so each emission re-seeds <see cref="AccessService.Context"/>
    /// on the dispatching thread before downstream Subscribe callbacks run. Without this,
    /// the response arrives on the hub action block (identity = receiving-hub address as
    /// hub-impersonation), and any post made from the Subscribe callback inherits the wrong
    /// identity — surfaces as <c>Access denied: user '&lt;cell-hub-path&gt;' lacks ...</c>.
    /// </summary>
    /// <summary>
    /// 🚨 <b>The one hop that stops a shared execution hub from serialising the whole mesh
    /// (#2543)</b> — applied ONLY to requests that declare
    /// <see cref="IDetachedResponseContinuation"/>.
    ///
    /// <para>A response subject is signalled from INSIDE the turn that handled the response, so Rx
    /// resumes the caller's chain on the responding hub's action block, inside that turn — and the
    /// turn cannot end until the chain does. On <c>portal/nodeops</c>, the mesh's ONE node-CRUD
    /// execution hub, that made every node write in the mesh queue behind one create's
    /// continuation: <c>Queue(buffer=1,…) Executing(CreateNodeResponse, …)</c> for a whole
    /// budget.</para>
    ///
    /// <para>🚨 <b>Why not for every request.</b> Doing it unconditionally was tried and is not
    /// safe. The action block is not only a serialiser — it is an ERROR BOUNDARY and a DISPOSAL
    /// FENCE, and an impersonation <c>AsyncLocal</c> is still in scope on it. Hopping every
    /// continuation off it crashed the test host repeatedly and broke several invariants nobody had
    /// written down. Narrowing the hop to the requests that demonstrably must not hold a shared hub
    /// keeps every other request's semantics exactly as they were.</para>
    ///
    /// <para>🚨 <b>Order matters:</b> this wraps the OUTSIDE of the identity restore, so
    /// <c>RestoreUserContextOnEmission</c>'s <c>.Do(SetContext)</c> is what runs first on the
    /// continuation's thread. Reversed, the chain would resume unauthenticated.</para>
    ///
    /// <para>And a continuation that has left the block can no longer be caught by the pump, so it
    /// is fenced: <see cref="GuardContinuationFaults"/> reports a throw and TERMINATES the sequence
    /// rather than letting it go unhandled on a scheduler thread and kill the process.</para>
    /// </summary>
    /// <param name="source">The response observable, identity already restored.</param>
    /// <param name="request">The request message, or null when it is not known.</param>
    private IObservable<IMessageDelivery> ContinueOffBlockIfDeclared(
        IObservable<IMessageDelivery> source, object? request)
        => request is IDetachedResponseContinuation
            ? GuardContinuationFaults(source.ObserveOn(PooledContinuationScheduler.Instance))
            : source;

    /// <summary>
    /// The block is an ERROR BOUNDARY, and a continuation that has left it is outside that
    /// boundary — an exception from a <c>Subscribe</c> callback would otherwise surface unhandled
    /// on a scheduler thread and take the process down.
    ///
    /// <para>🚨 It REPORTS <b>and TERMINATES</b>. A first version only reported, which turned a
    /// crash into a HANG: the caller's sequence stayed open forever waiting for an emission that
    /// could never come. Rx's own contract is that a throwing observer ends the subscription, and
    /// that is what the pump did too — it logged the fault and failed the delivery, so the caller
    /// got an answer.</para>
    /// </summary>
    /// <param name="source">The continuation, already hopped off the block.</param>
    private IObservable<IMessageDelivery> GuardContinuationFaults(IObservable<IMessageDelivery> source)
        => Observable.Create<IMessageDelivery>(observer => source.Subscribe(
            value =>
            {
                try
                {
                    observer.OnNext(value);
                }
                catch (Exception ex)
                {
                    ReportContinuationFault(ex);
                    Guarded(() => observer.OnError(ex));
                }
            },
            error => Guarded(() => observer.OnError(error)),
            () => Guarded(observer.OnCompleted)));

    /// <summary>Runs one observer callback, routing anything it throws. See
    /// <see cref="GuardContinuationFaults"/> for why this is the block's boundary and not a
    /// swallow.</summary>
    /// <param name="deliver">The observer callback to run.</param>
    private void Guarded(Action deliver)
    {
        try
        {
            deliver();
        }
        catch (Exception ex)
        {
            ReportContinuationFault(ex);
        }
    }

    /// <summary>Says what a continuation fault was, at the level its cause deserves.</summary>
    /// <param name="ex">The fault an observer callback threw.</param>
    private void ReportContinuationFault(Exception ex)
    {
        if (RunLevel >= MessageHubRunLevel.ShutDown)
            logger.LogDebug(ex,
                "{Address}: a response continuation raced this hub's teardown — transient. The "
                + "continuation runs off the action block, so it can outlive the scope it "
                + "resolves from; the caller's subscription is already going away.", Address);
        else
            logger.LogError(ex,
                "{Address}: a response continuation threw. It runs off the action block, so this "
                + "did not fault a turn — it would otherwise have gone unhandled on a scheduler "
                + "thread and killed the process. The throw is in the code that subscribed to "
                + "hub.Observe(...), not in the hub.", Address);
    }

    private IObservable<IMessageDelivery> RestoreUserContextOnEmission(
        IObservable<IMessageDelivery> source, AccessContext? capturedCtx)
    {
        if (capturedCtx is null)
            return source;
        // Restore-on-Finally pattern (2026-05-22): set Context during the
        // subscription so emission-side code (Subscribe callbacks, downstream
        // posts) runs under the captured identity; restore the prior value
        // when the observable completes/errors/disposes. Without this, the
        // captured identity leaked into the caller's AsyncLocal — symptom:
        // McpUpdate tests showed user1's identity used even after
        // LoginWithToken switched to user2, because the earlier user1 call
        // had set Context=user1 on the test thread and never cleared it.
        return Observable.Defer<IMessageDelivery>(() =>
        {
            var prev = accessService.Context;
            accessService.SetContext(capturedCtx);
            return source
                .Do(_ => accessService.SetContext(capturedCtx))
                .Finally(() => accessService.SetContext(prev));
        });
    }

    private IObservable<IMessageDelivery> ApplyTimeout(
        IObservable<IMessageDelivery> source,
        string requestType,
        Address? target,
        string messageId)
        => source.Timeout(Configuration.RequestTimeout,
            System.Reactive.Linq.Observable.Defer<IMessageDelivery>(() =>
                System.Reactive.Linq.Observable.Throw<IMessageDelivery>(
                    new TimeoutException(BuildTimeoutMessage(requestType, target, messageId)))));

    /// <summary>
    /// Names the request that timed out, AND this hub's own state at the moment it gave up.
    ///
    /// <para>🚨 <b>The previous message blamed the target without ever looking at the caller.</b> It
    /// ended "The request may have been undeliverable or the target hub was not found" — two
    /// buckets, asserted as though they were exhaustive. They are not, and the third possibility is
    /// the one a reader most needs to rule out first: <b>this hub never PROCESSED a response that
    /// did arrive</b>, because its own action block was busy, gated, or backed up. A single-threaded
    /// actor that is wedged looks, from inside, exactly like a peer that never answered.</para>
    ///
    /// <para>Measured on prod 2026-09-02: a document read failed with that message naming
    /// <c>cache/…</c> as the waiting hub and a node path as the target. Both buckets it offered were
    /// wrong — the node existed (version 53, edited the previous evening) and its hub resolved fine;
    /// one per-node hub was wedged and a <c>recycle</c> cleared it with no data loss. The message
    /// sent the reader to "does this node exist?", which is the one question that was not in doubt,
    /// and the deployment ships no logs to Log Analytics so there was nothing else to read
    /// (MeshWeaver#2896, open for weeks as "write verdict unconfirmed" for exactly this reason).</para>
    ///
    /// <para>So the message now carries the caller's <c>RunLevel</c> and queue snapshot — the same
    /// fields the disposal diagnostic prints — and states the classification EXPLICITLY, including
    /// an explicit unknown. A diagnostic that offers two buckets when there are three teaches the
    /// reader to pick the nearer one; naming what this hub could and could not observe is what makes
    /// it a measurement rather than a guess.</para>
    ///
    /// <para>🚨 <b>Three corrections, all measured on MeshWeaver#1174 (424 occurrences over a
    /// month, and two triages sent to the wrong place by this very sentence).</b></para>
    ///
    /// <para><b>(1) The idleness claim was an INSTANTANEOUS SAMPLE asserted over an INTERVAL.</b>
    /// The queue snapshot is read at the moment the wait gives up, and the sentence generalised it
    /// across the whole <see cref="MessageHubConfiguration.RequestTimeout"/>: a hub saturated for
    /// 59 seconds that drained in the 60th printed <i>"This hub was idle while waiting, so it
    /// processed everything delivered to it"</i> — a claim the sample cannot support, and exactly
    /// the class of answer that reads like a pass. <see cref="Version"/> is incremented once per
    /// message handled, so the difference against the value captured at registration
    /// (<c>PendingCallback.RegisteredAtVersion</c>) is the interval fact the sample is not: how
    /// many messages this hub handled while the request was outstanding.</para>
    ///
    /// <para><b>(2) On a SELF-ADDRESSED request, all three candidates and the discriminator are
    /// inapplicable.</b> The mesh's node CRUD runs on <c>portal/nodeops-{meshId}</c>, and
    /// <c>MeshService</c> ISSUES those requests on that same hub — so sender and target are one
    /// hub and the delivery never leaves it. There is then no routing leg to lose it and no reply
    /// leg to lose the answer; "the target's own RunLevel and queue" are the numbers already
    /// printed in this very sentence. Production read it literally and concluded the request
    /// "never reached the queue" — which an empty queue at the give-up instant does not imply,
    /// because the canonical mesh handlers return <c>Processed()</c> in a millisecond and owe
    /// their reply from a DETACHED observable, leaving the queue empty while the reply is still
    /// owed.</para>
    ///
    /// <para><b>(3) It said "this message cannot distinguish them" while the answer was one call
    /// away.</b> <see cref="RequestFateLedger"/> is per hub TREE and records every stage this
    /// delivery passed through — intake, gate, routing, handler entry, the handler's own detached
    /// stages, the reply's journey — ending in a verdict that names which shape this is. The hub
    /// building this message owns that ledger. Printing it costs one lookup and removes the whole
    /// "go and measure the other end" step for every request whose target is in this tree (and for
    /// a target outside it, the ledger says so in as many words).</para>
    /// </summary>
    private string BuildTimeoutMessage(string requestType, Address? target, string messageId)
    {
        var snapshot = (messageService is MessageService ms)
            ? ms.GetQueueSnapshot()
            : (Buffer: -1, Deferred: -1, DrainsInFlight: -1, OpenGates: -1, Draining: false,
               CurrentMessage: (string?)null, CurrentMessageElapsedMs: 0L,
               DrainsAwaitingScheduler: -1L);

        // The discriminator. A hub that was IDLE while waiting genuinely heard nothing: the silence
        // is upstream. A hub that was busy, gated, or holding queued work cannot make that claim —
        // the response may have arrived and be sitting behind the very work that delayed it.
        var callerBusy = snapshot.Buffer > 0
                         || snapshot.Deferred > 0
                         || snapshot.OpenGates > 0
                         || snapshot.CurrentMessage is not null;

        // (1) The interval the sample cannot see. `null` only when the entry is already gone —
        // then say "unknown" rather than print a number derived from nothing.
        long? handledWhileWaiting = null;
        lock (responseSubjects)
        {
            if (responseSubjects.TryGetValue(messageId, out var pending))
                handledWhileWaiting = Version - pending.RegisteredAtVersion;
        }
        var handled = handledWhileWaiting is { } n
            ? $"handledWhileWaiting={n}"
            : "handledWhileWaiting=unknown";

        var state =
            $"This hub: RunLevel={RunLevel} Queue(buffer={snapshot.Buffer},deferred={snapshot.Deferred}," +
            $"openGates={snapshot.OpenGates},drainsInFlight={snapshot.DrainsInFlight}," +
            $"draining={snapshot.Draining},{handled})" +
            (snapshot.CurrentMessage is not null
                ? $" Executing({snapshot.CurrentMessage}, {snapshot.CurrentMessageElapsedMs}ms)"
                : string.Empty);

        // (2) Sender and target are the same hub — the delivery never left, so two of the three
        // candidates below cannot happen and the third's discriminator is already printed.
        var selfAddressed = target is not null && (target with { Host = null }).Equals(Address);

        var verdict = selfAddressed
            ? "🚨 THIS HUB IS ALSO THE TARGET, so the request never left it: there is no routing leg "
              + "that could have lost it and no reply leg that could have lost the answer, and "
              + "\"the target's own RunLevel and queue\" are the numbers printed above. An empty "
              + "queue here does NOT mean the request was never handled — the canonical mesh "
              + "handlers return Processed() at once and owe their reply from a DETACHED "
              + "observable, so a handler that ran and has not yet produced a terminal looks "
              + "exactly like one that never ran. What is left is: the delivery was refused at "
              + "this hub's own intake, or a handler took it and the work that owes the reply "
              + "produced no terminal. The trail below says which."
            : callerBusy
                ? "🚨 THIS HUB WAS NOT IDLE while waiting, so it cannot attribute the silence upstream: " +
                  "a response may have arrived and be queued behind the work above. Investigate THIS hub " +
                  "before the target."
                : "This hub is idle AT THE MOMENT IT GAVE UP — an instantaneous sample, which is why " +
                  "handledWhileWaiting above is printed beside it: that is the interval fact, and a hub " +
                  "that handled many messages was not idle throughout however empty its queue is now. " +
                  "Cause is UNKNOWN between: the target never received the " +
                  "request (routing), the target received it and is wedged (a per-node hub that stops " +
                  "answering — MeshWeaver#2896), or the target answered and the reply was lost. Queue " +
                  "state alone cannot distinguish them; the trail below can, and so can the target's " +
                  "own RunLevel and queue.";

        // (3) The stage trail this hub tree already recorded for THIS request, ending in its own
        // verdict. Never throws; names its own absence when the target lives outside this tree.
        var trail = this.DescribeRequestFate(messageId);

        return
            $"No response received in hub {Address} within {Configuration.RequestTimeout} " +
            $"for request {requestType} (id={messageId}) → target {target?.ToString() ?? "<unset>"}. " +
            $"{state}. {verdict} Trail: {trail}";
    }

    private System.Reactive.Subjects.AsyncSubject<IMessageDelivery> GetOrAddResponseSubject(
        string messageId,
        string requestType = "<unknown>",
        Address? target = null,
        string? diagnosticKey = null)
    {
        lock (responseSubjects)
        {
            if (RunLevel >= MessageHubRunLevel.ShutDown)
            {
                var disposed = new System.Reactive.Subjects.AsyncSubject<IMessageDelivery>();
                disposed.OnError(new ObjectDisposedException(nameof(MessageHub),
                    $"Hub {Address} is shutting down — cannot register new response subject for {messageId}."));
                return disposed;
            }
            if (!responseSubjects.TryGetValue(messageId, out var entry))
            {
                entry = new PendingCallback(
                    new System.Reactive.Subjects.AsyncSubject<IMessageDelivery>(),
                    requestType,
                    target,
                    Stopwatch.GetTimestamp(),
                    Version,
                    diagnosticKey);
                responseSubjects[messageId] = entry;
                // THE one place a hub starts awaiting a reply — so it is also the one place the
                // handler-side trail starts. "Tracked" and "awaited" are the same set by
                // construction, which is what bounds the ledger.
                requestFates.Track(messageId, Address, requestType, target);
                logger.LogDebug("Adding response subject for {Id} (type={Type}, target={Target})",
                    messageId, requestType, target);
            }
            return entry.Subject;
        }
    }


    /// <summary>
    /// 🚨 True when <paramref name="delivery"/> is a request one of THIS hub's hosted hubs ACCEPTED
    /// before its own teardown began, now on its way OUT through this hub — which this hub must still
    /// carry while it is disposing that very hub (Systemorph/MeshWeaver#3986).
    ///
    /// <para><b>The defect this closes.</b> A parent in <see cref="MessageHubRunLevel.DisposeHostedHubs"/>
    /// refused every transit delivery (tier 2 of the teardown intake gate, and the route-up check in
    /// <c>HierarchicalRouting</c>), on the stated ground that "the children are going down with it".
    /// But that phase is exactly when the children are ASKED to go down: each one's
    /// <c>ShutdownRequest</c> queues FIFO behind the work it already accepted, and that work runs
    /// first — "teardown lets accepted work FINISH" (<c>Doc/Architecture/TeardownLayers</c>). So a
    /// hosted hub still holding accepted outbound work when its parent reached this phase had that
    /// work refused at the only door it has. Measured: a person's click, accepted by the stream's
    /// <c>sync/{id}</c> hub while its queue was busy, then the per-circuit portal hub disposed on
    /// circuit close — the click was refused with <c>cannot route ClickedEvent … its parent hub … is
    /// shutting down (RunLevel=DisposeHostedHubs)</c> and never reached the owner
    /// (<c>UserActionQueuedBehindABusySyncHubTest</c>).</para>
    ///
    /// <para><b>Why carrying it is safe, by construction rather than by a wait.</b> This hub does not
    /// reach <see cref="MessageHubRunLevel.ShutDown"/> until every hosted hub has signalled
    /// <c>DisposalCompleted</c>, and a hosted hub cannot complete before it has handed its accepted
    /// backlog up — so the delivery is in this hub's queue before this hub's own ShutDown phase is
    /// even posted, and leaves through its router while that router is still running. The answer comes
    /// back through the reply exemption the gate already has, into the child's <c>Quiescing</c> drain,
    /// which is waiting for exactly it. Nothing new waits, nothing is timed.</para>
    ///
    /// <para><b>Deliberately narrow — every clause is a reason, not a heuristic:</b>
    /// <list type="bullet">
    ///   <item>only while this hub is IN <see cref="MessageHubRunLevel.DisposeHostedHubs"/> — before it
    ///     the gate is open, after it there are no children left whose work could still be owed;</item>
    ///   <item>only a request its ORIGINATING hub holds a live response callback for — the receipt that
    ///     hub's <c>Quiescing</c> drain is waiting on. A one-way <see cref="IRequest"/> posted without
    ///     <c>Observe</c> has nobody waiting and keeps its historical drop, as does fire-and-forget; a
    ///     REPLY already has its own exemption;</item>
    ///   <item>only TRANSIT — a request addressed to THIS hub is new work for a hub that is going away
    ///     and stays refused;</item>
    ///   <item>only while the hosted hub handing it up is still BELOW <see cref="MessageHubRunLevel.Quiescing"/>.
    ///     That is the acceptance fence, and it is structural rather than a snapshot: routing runs inside
    ///     that hub's own turn, and it leaves <see cref="MessageHubRunLevel.Started"/> only by handling
    ///     its own <c>ShutdownRequest</c>, which its <c>Dispose()</c> posts FIFO. So a delivery routed from
    ///     a hub still below <c>Quiescing</c> was queued AHEAD of that request — accepted before its
    ///     teardown — and one it takes on afterwards queues BEHIND it, is routed from <c>Quiescing</c>, and
    ///     is refused here (<c>UserActionQueuedBehindABusySyncHubTest</c> pins both sides);</item>
    ///   <item>only when this hub's PARENT still routes — in a whole-tree teardown the parent is going too,
    ///     the request could only be dropped one hop later, and the requester is better served by the
    ///     immediate transient refusal it gets today than by waiting out its quiesce budget for it.</item>
    /// </list></para>
    /// </summary>
    /// <param name="delivery">The delivery being routed up to (or arriving at) this hub.</param>
    /// <returns><c>true</c> when this hub must carry it despite disposing its hosted hubs.</returns>
    internal bool CarriesAcceptedWorkOfAHostedHub(IMessageDelivery delivery)
    {
        if (RunLevel != MessageHubRunLevel.DisposeHostedHubs
            || delivery.Properties.ContainsKey(PostOptions.RequestId)
            || delivery.Target is not { } target
            || (target with { Host = null }).Equals(Address with { Host = null })
            || (messageService as MessageService)?.ParentHub is not { RunLevel: < MessageHubRunLevel.DisposeHostedHubs })
            return false;
        var (handedUpBy, originator) = HostedHubsThatSent(delivery.Sender);
        return handedUpBy is { RunLevel: < MessageHubRunLevel.Quiescing }
               && originator is MessageHub waiting
               && waiting.AwaitsResponseTo(delivery.Id);
    }

    /// <summary>
    /// True while this hub holds a live response callback for the request it posted with
    /// <paramref name="messageId"/> — i.e. something here is still waiting for that answer.
    /// </summary>
    private bool AwaitsResponseTo(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return false;
        lock (responseSubjects)
            return responseSubjects.ContainsKey(messageId);
    }

    /// <summary>
    /// The hosted hub of THIS hub that handed <paramref name="sender"/>'s delivery up, and the hub
    /// that originally posted it (the same hub unless it came from further down), or <c>null</c>s.
    /// The route-up stamps each parent onto the OUTERMOST host of the sender
    /// (<c>AddressExtensions.WithHost</c>) — except a mesh parent, which is not stamped — so once
    /// this hub's own stamp is peeled off, the outermost address left is the hosted hub that handed
    /// it up and the innermost is the originator. Looked up level by level, never created.
    /// </summary>
    private (IMessageHub? HandedUpBy, IMessageHub? Originator) HostedHubsThatSent(Address? sender)
    {
        var levels = ImmutableList<Address>.Empty;
        for (var level = sender; level is not null; level = level.Host)
            levels = levels.Add(level with { Host = null });
        var outermost = levels.Count - 1;
        if (outermost >= 0 && levels[outermost].Equals(Address with { Host = null }))
            outermost--;
        if (outermost < 0)
            return (null, null);
        var handedUpBy = GetHostedHub(levels[outermost], c => c, HostedHubCreation.Never);
        var originator = handedUpBy;
        for (var i = outermost - 1; originator is not null && i >= 0; i--)
            originator = originator.GetHostedHub(levels[i], HostedHubCreation.Never);
        return (handedUpBy, originator);
    }

    private IObservable<IMessageDelivery> ExecuteRequest(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        if (delivery.Message is not ExecutionRequest er)
            return Observable.Return(delivery);
        // Genuinely-async leaf (the caller-supplied Action) → delegate to the
        // pool and replay the result; everything around it stays synchronous.
        return DeliveryObservable.Run(async _ =>
        {
            await er.Action.Invoke(cancellationToken);
            return delivery.Processed();
        });
    }

    /// <summary>
    /// True when <paramref name="delivery"/> is a reply correlated (via
    /// <see cref="PostOptions.RequestId"/>) to a request THIS hub issued and is still
    /// awaiting — i.e. there is a live <see cref="responseSubjects"/> entry for it.
    /// <para>
    /// Used by the init-gate deferral (<c>MessageService</c>) to NEVER defer a reply the
    /// hub is waiting on behind its own initialization gate. Deferring it deadlocks the
    /// hub against its own awaited response — e.g. a data loader that reads a cross-hub
    /// node during <c>DataContextInit</c>: the reply routes back on-target while the gate
    /// is still closed, gets queued, and the gate can't open until the load (waiting on
    /// that reply) completes. This generalises the existing <c>DeliveryFailure</c> bypass
    /// to the SUCCESS reply, which suffers the identical deadlock.
    /// </para>
    /// </summary>
    internal bool IsAwaitedResponse(IMessageDelivery delivery)
    {
        if (!delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || requestId?.ToString() is not { Length: > 0 } requestIdString)
            return false;
        lock (responseSubjects)
            return responseSubjects.ContainsKey(requestIdString);
    }

    private IObservable<IMessageDelivery> HandleCallbacks(
        IMessageDelivery delivery,
        CancellationToken cancellationToken
    )
    {
        // Per-response hot path. Cache type name + gate by IsEnabled to skip
        // GetType().Name recomputes and params boxing when logger is off.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        var debugEnabled = logger.IsEnabled(LogLevel.Debug);
        string? messageTypeName = (traceEnabled || debugEnabled) ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_HANDLE_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        if (
            !delivery.Properties.TryGetValue(PostOptions.RequestId, out var requestId)
            || requestId.ToString() is not { } requestIdString
        )
        {
            if (traceEnabled)
                logger.LogTrace("MESSAGE_FLOW: HUB_NO_CALLBACKS | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                    messageTypeName, Address, delivery.Id);
            return Observable.Return(delivery);
        }

        System.Reactive.Subjects.AsyncSubject<IMessageDelivery> subject;
        lock (responseSubjects)
        {
            if (!responseSubjects.Remove(requestIdString, out var entry))
            {
                // No live callback for this correlation. Record it on the trail (if some OTHER hub
                // is still awaiting the same id — the shape where a reply lands on the wrong hub)
                // before treating the response as consumed.
                requestFates.Find(requestIdString)?.Add(
                    $"RESPONSE_ARRIVED_NO_SUBJECT type={delivery.Message.GetType().Name}", Address);
                if (debugEnabled)
                    logger.LogDebug("No subject found for response message {MessageType} (ID: {MessageId}) - treating as processed",
                        messageTypeName, delivery.Id);
                return Observable.Return(delivery.Processed());
            }
            subject = entry.Subject;
            // The callback is resolving now — the trail has done its job.
            requestFates.Untrack(requestIdString);
        }

        if (debugEnabled)
            logger.LogDebug("Dispatching response to subject | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);

        if (delivery.Message is DeliveryFailure failure)
        {
            subject.OnError(new DeliveryFailureException(failure));
        }
        else
        {
            subject.OnNext(delivery);
            subject.OnCompleted();
        }

        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_CALLBACKS_COMPLETE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);
        // Stamp "a live Observe callback consumed this" so later rules can tell an AWAITED
        // response apart from an un-awaited one. The rule chain keeps running after this rule
        // (HandleCallbacks runs first), and the portal's DeliveryFailure→modal handler must NOT
        // re-surface a failure the call site's OnError already handled (e.g. StartThread's
        // user-partition fallback) — that double-report was the raw "Access denied … lacks
        // Thread permission" modal popping despite the fallback succeeding.
        return Observable.Return(delivery.Processed().SetProperty(PostOptions.CallbackDispatched, true));
    }

    Address IMessageHub.Address => Address;

    /// <summary>
    /// Posts a message into the mesh via the message service for routing/handling, applying optional
    /// delivery options. The side effect is the dispatch itself.
    /// </summary>
    /// <typeparam name="TMessage">The message payload type.</typeparam>
    /// <param name="message">The message payload to send.</param>
    /// <param name="configure">Optional configuration of the delivery (target, sender, response-correlation, message id).</param>
    /// <returns>The created delivery wrapping <paramref name="message"/>, or <c>null</c> if it was not posted.</returns>
    public IMessageDelivery<TMessage>? Post<TMessage>(
        TMessage message,
        Func<PostOptions, PostOptions>? configure = null
    )
    {
        var options = new PostOptions(Address);
        if (configure != null)
            options = configure(options);

        // Per-message hot path. typeof(TMessage).Name is JIT-folded so it's free,
        // but params object[] boxing of options.Target / Sender / result.Id is not.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_POST | {MessageType} | Hub: {Address} | Target: {Target} | Sender: {Sender}",
                typeof(TMessage).Name, Address, options.Target, options.Sender);

        // Log only important messages during disposal
        if (IsDisposing && (message is ShutdownRequest || logger.IsEnabled(LogLevel.Debug)))
        {
            logger.LogDebug("Posting {MessageType} during disposal from {Sender} to {Target} in hub {Address}",
                typeof(TMessage).Name, options.Sender, options.Target, Address);
        }

        var result = (IMessageDelivery<TMessage>?)messageService.Post(message, options);
        ReportRouterTrafficOrigin(result);
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_POST_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | Target: {Target}",
                typeof(TMessage).Name, Address, result?.Id, options.Target);
        return result;
    }

    // One report per (role, message type) for this hub's lifetime, keyed independently of the
    // receiver-side dictionary: the two sites answer different questions and one must not mute the
    // other.
    private readonly ConcurrentDictionary<string, byte> routerTrafficOriginReported = new();

    /// <summary>
    /// 🚨 The ORIGIN half of the <c>ROUTER_TRAFFIC</c> detector: the same rule, evaluated where the
    /// delivery is CREATED, so the line can name the CALL SITE.
    ///
    /// <para><b>Why the receiver-side report is not enough.</b>
    /// <see cref="ReportRouterTraffic"/> fires inside <see cref="DeliverMessage"/>, on the hub the
    /// delivery is addressed to — a stack there is the routing machinery, not the code that made the
    /// mistake. So every production line carries two addresses and nothing else, and the reader has
    /// to guess which of the mesh's many root-hub callers produced it. On <c>memex</c> that guess
    /// stayed unresolved through four re-filings and 41,087 lines
    /// (<see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see>, and #1113 /
    /// #1121 / #1136 before it) — a detector that says a rule was broken but not by whom cannot
    /// close the issue it opens. The payload type does not help either: a delivery that crossed a
    /// silo arrives packed, so the receiver reports the honest but useless <c>RawJson</c>.</para>
    ///
    /// <para><b>Cost.</b> The rule is two ordinal compares and a type check, the same check
    /// <see cref="DeliverMessage"/> already runs per inbound message. The STACK is captured only
    /// after the rule has fired AND the per-(role, type) key was new, so a hub pays for it at most
    /// once per message type per role per lifetime — two extra lines per process for #1140's shape,
    /// against the tens of thousands the receiver side emits (its count scales with the number of
    /// per-node hubs that see the traffic; this one does not).</para>
    ///
    /// <para>Same exclusions as the receiver side, because it is literally the same predicate: a
    /// heartbeat is routing liveness, and a response the router posts is the undeliverable-mail NACK
    /// — routing's own duty (see <see cref="RouterTrafficRule.RoleOf(string?, string?, object?, bool)"/>).</para>
    ///
    /// <para>🚨 <b>The remedy the line prints is ROLE-DEPENDENT, and printing one remedy for both
    /// roles manufactured a misdiagnosis</b>
    /// (<see href="https://github.com/Systemorph/MeshWeaver/issues/4697">#4697</see>). For
    /// <c>sender</c> the call site below IS the thing to move, and there are THREE seams to move it
    /// onto — not the two this line named until #4697 — which are not interchangeable:
    /// <c>ReadIssuingHub()</c> registers no handlers by design, so a stream SUBSCRIPTION hopped
    /// onto it stops reporting and stops receiving data in the same breath, the worst available
    /// failure mode because the instrument goes quiet with the subject (#4614).</para>
    ///
    /// <para>🚨 <b>And <c>target</c> is TWO populations with opposite fixes, which is why the line
    /// asks a question there instead of asserting one.</b> The role fires whenever the delivery is
    /// ADDRESSED at the router, and that covers real work someone SENT to it — see the
    /// <c>isResponse</c> remark on
    /// <see cref="RouterTrafficRule.RoleOf(string?, string?, object?, bool)"/>: <i>"real work SENT
    /// TO the router is still reported at request time via the target role"</i> — as well as the
    /// reply/fan-out case. In the first the call site CHOSE the destination and is the offender
    /// (<c>NodeOperationTarget()</c>); in the second the target was read off an incoming request or
    /// subscription (<c>request.Subscriber</c>, <c>ResponseFor(delivery)</c>) and the call site is
    /// the innocent answering half, so the hub to move is the SUBSCRIBER. Nothing at the detector
    /// separates them — a <c>DataChangedEvent</c> fan-out carries no request-id, so
    /// <c>isResponse</c> is not that discriminator — but the reader AT the call site answers it in
    /// one look, which is why the line hands them that test rather than a verdict.
    /// <c>sender AND target</c> means both halves apply. #4697 was auto-filed off this line and its
    /// "probable cause" repeated the printed two-seam advice verbatim about a target-role fan-out,
    /// naming an innocent frame in <c>JsonSynchronizationStream</c>; the real defect was one hub
    /// away and already fixed. (Copilot on #4712 caught the first draft of this fix asserting the
    /// reply case just as confidently — the same defect, pointed the other way.)
    /// <c>RouterOriginAdviceNamesEverySeamGuard</c> holds the text to the seam vocabulary the two
    /// <c>src/</c> ratchets read with.</para>
    /// </summary>
    /// <param name="delivery">The delivery just created by this post, or <c>null</c> if none was.</param>
    private void ReportRouterTrafficOrigin(IMessageDelivery? delivery)
    {
        if (delivery is null)
            return;

        // 🚨 A TARGET-LESS delivery is handled by the POSTING hub and never leaves it, so "the mesh
        // hub is an END of this delivery" is trivially true of it and says nothing. Reporting it
        // fires on every mesh hub's own `InitializeHubRequest` — measured, on the first run of this
        // detector — and a line that appears on every boot is the kind that gets muted, taking the
        // real ones with it. The receiver side draws the same boundary, structurally: a self-post
        // never reaches `DeliverMessage`, which is why ROUTER_TRAFFIC has never reported one.
        //
        // The class that goes with it — WORK self-posted on the router, e.g. a target-less
        // `CreateNodeRequest` executing on the router's action block — is a different symptom with
        // its own instruments (MeshExtensions.NodeOperationTarget, which stops it being posted, and
        // the turn-loop snapshot, which measures the block). It was never covered by this detector
        // at either site, and quietly folding it in here would cost the detector its signal.
        if (delivery.Target is null)
            return;

        // The DELIVERY's own ends, exactly as the receiver side reads them — never Address, which
        // here happens to be one of them but would silently stop being so for a post that names an
        // explicit Sender.
        var role = RouterTrafficRule.RoleOf(delivery.Target?.Type, delivery.Sender?.Type, delivery.Message,
            delivery.Properties.ContainsKey(PostOptions.RequestId));
        if (role is null)
            return;

        var messageType = delivery.Message?.GetType().Name ?? "(null)";
        if (!routerTrafficOriginReported.TryAdd($"{role}:{messageType}", 0))
            return;

        logger.LogError(
            "ROUTER_TRAFFIC ORIGIN: {MessageType} was POSTED with the mesh hub as {Role} (sender: "
            + "{Sender}, target: {Target}). The mesh hub is the ROUTER and must not be an end of a "
            + "work delivery. If the role above includes 'sender', this post LEFT the router: hop "
            + "it onto the seam that matches what this delivery IS — "
            + "MeshExtensions.NodeOperationIssuingHub() for a node LIFECYCLE write, "
            + "MeshExtensions.ReadIssuingHub() for a bounded one-shot READ, "
            + "MeshExtensions.StreamSubscribingHub() for a remote stream SUBSCRIPTION. The three "
            + "are NOT interchangeable and the wrong one fails SILENTLY: ReadIssuingHub() "
            + "registers no handlers by design, so a subscription hopped onto it stops reporting "
            + "AND stops receiving data (#4614). If the role above includes 'target', ask where "
            + "that target CAME FROM, because the two cases have opposite fixes: if this call site "
            + "CHOSE the router as the destination, the call site is the offender — address the "
            + "owning node instead (MeshExtensions.NodeOperationTarget()); if the target was read "
            + "off an incoming request or subscription (request.Subscriber, ResponseFor(delivery) "
            + "— SubscribeAck, DataChangedEvent, StreamErrorEvent and StreamEndedEvent all are), "
            + "then this call site is the innocent answering half and the hub to move is the one "
            + "that SUBSCRIBED or REQUESTED (#4697). 'sender AND target' means both apply. "
            + "Reported once per role+type for this hub. Call site:\n{CallSite}",
            messageType, role, delivery.Sender?.ToString() ?? "(none)",
            delivery.Target?.ToString() ?? "(none)", DescribeCallSite());
    }

    /// <summary>
    /// The frames worth printing for <see cref="ReportRouterTrafficOrigin"/>: the posting code, with
    /// this hub's own plumbing dropped off the top so the first line is the caller rather than
    /// <c>Post</c> itself. Bounded — a stack dump nobody reads is as unhelpful as no stack at all.
    /// </summary>
    private static string DescribeCallSite()
    {
        const int MaxFrames = 12;
        var frames = new StackTrace(fNeedFileInfo: true).GetFrames();
        var lines = new List<string>(MaxFrames);
        var started = false;
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method is null)
                continue;
            // Drop the leading frames inside this type (Post, ReportRouterTrafficOrigin and this
            // method) — they are the same three lines on every report and push the answer down.
            if (!started && method.DeclaringType == typeof(MessageHub))
                continue;
            started = true;
            var file = frame.GetFileName();
            var where = file is { Length: > 0 } ? $" ({Path.GetFileName(file)}:{frame.GetFileLineNumber()})" : string.Empty;
            lines.Add($"   at {method.DeclaringType?.FullName}.{method.Name}{where}");
            if (lines.Count >= MaxFrames)
                break;
        }
        return lines.Count > 0 ? string.Join("\n", lines) : "   (no managed frames)";
    }

    /// <summary>
    /// Inbound entry point: marks <paramref name="delivery"/> as Submitted and routes it onto the
    /// hub's action block for processing. Called by the routing layer, not application code.
    /// </summary>
    /// <param name="delivery">The routed delivery to process on this hub.</param>
    /// <returns>The delivery in its post-routing state.</returns>
    public IMessageDelivery DeliverMessage(IMessageDelivery delivery)
    {
        // Per-inbound hot path. Cache type name + gate by IsEnabled.
        var traceEnabled = logger.IsEnabled(LogLevel.Trace);
        string? messageTypeName = traceEnabled ? delivery.Message.GetType().Name : null;
        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE | {MessageType} | Hub: {Address} | MessageId: {MessageId}",
                messageTypeName, Address, delivery.Id);
        MessageTrace.Write($"hub={Address} msg={delivery.Message?.GetType().Name} id={delivery.Id} HUB.DeliverMessage ENTER state={delivery.State}");

        ReportRouterTraffic(delivery);

        var ret = delivery.ChangeState(MessageDeliveryState.Submitted);
        var result = messageService.RouteMessageAsync(ret, default);

        if (traceEnabled)
            logger.LogTrace("MESSAGE_FLOW: HUB_DELIVER_MESSAGE_RESULT | {MessageType} | Hub: {Address} | MessageId: {MessageId} | State: {State}",
                messageTypeName, Address, delivery.Id, result.State);
        MessageTrace.Write($"hub={Address} msg={delivery.Message?.GetType().Name} id={delivery.Id} HUB.DeliverMessage EXIT state={result.State}");
        return result;
    }


    // One report per (role, message type) for this hub's lifetime. Node CRUD targets the router on
    // EVERY write today, so an un-deduped line would be a storm — and a storm gets muted, which is
    // how this stayed invisible in the first place.
    private readonly ConcurrentDictionary<string, byte> routerTrafficReported = new();

    /// <summary>
    /// 🚨 Logs an ERROR when the ROOT MESH HUB is the sender or the target of a delivery.
    ///
    /// <para>The mesh hub is the mesh's ROUTER and nothing else. Work executed on its action block —
    /// node CRUD above all — competes with routing itself: a burst of creates starves real
    /// <c>SubscribeRequest</c> traffic and every node op then times out, which is a portal-wide wedge
    /// (prod 2026-06-11: "11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/&lt;self&gt; stale
    /// &gt;60s while real user SubscribeRequests starved"). Work belongs on a hub of its own — the
    /// session portal hub for REST / Blazor / MCP, the dedicated <c>import/{id}</c> hub for bulk
    /// imports.</para>
    ///
    /// <para>This is a DETECTOR, not a guard: it never blocks a delivery. Every violating path must
    /// stay working while it is migrated — and must stay VISIBLE, because the failure is silent until
    /// it is catastrophic. The leaked-callback CI failure that surfaced it read as a flaky test, not
    /// as the router doing someone else's job.</para>
    ///
    /// <para>🚨 The ends are the DELIVERY's own — <c>delivery.Target</c> and <c>delivery.Sender</c> —
    /// never <see cref="Address"/>, the hub that happens to be handling it. Those differ on every
    /// ROUTED message: <c>HierarchicalRouting</c> sends a hosted hub's non-local delivery UP via
    /// <c>parentHub.DeliverMessage(delivery)</c>, and for essentially every hub in the process that
    /// parent is the root mesh hub — so keying on <see cref="Address"/> made the router's ordinary
    /// FORWARDING look like the router doing work, and reported every HOP as a violation ("validate a
    /// token + read a node" alone emitted five). A detector that cries wolf on the mesh's actual job
    /// is the one that gets muted, taking the real reports with it.</para>
    /// </summary>
    private void ReportRouterTraffic(IMessageDelivery delivery)
    {
        // The rule itself is a pure predicate — see RouterTrafficRule, where it is unit-tested.
        // Target = where the delivery is ADDRESSED, not where it is currently being handled.
        // isResponse: the RequestId correlation marks a delivery that ANSWERS a request — the shape
        // of the routing layer's own undeliverable-mail NACK, which posts from the mesh hub via
        // ResponseFor (see RouterTrafficRule.RoleOf's isResponse doc).
        var role = RouterTrafficRule.RoleOf(delivery.Target?.Type, delivery.Sender?.Type, delivery.Message,
            delivery.Properties.ContainsKey(PostOptions.RequestId));
        if (role is null)
            return;

        var messageType = delivery.Message?.GetType().Name ?? "(null)";
        if (!routerTrafficReported.TryAdd($"{role}:{messageType}", 0))
            return;

        // Log BOTH ends of the DELIVERY explicitly — never this hub's own address, which on a routed
        // message is merely the hop the delivery is passing through and sends the reader hunting an
        // innocent hub.
        logger.LogError(
            "ROUTER_TRAFFIC: {MessageType} has the mesh hub as {Role} (sender: {Sender}, target: {Target}). "
            + "The mesh hub is the ROUTER and must not execute work — it belongs on a hub of its own "
            + "(session portal hub for REST/Blazor/MCP, import hub for bulk imports). Reported once per "
            + "role+type for this hub.",
            messageType, role, delivery.Sender?.ToString() ?? "(none)", delivery.Target?.ToString() ?? "(none)");
    }

    /// <summary>
    /// Resolves (and, depending on <paramref name="create"/>, creates) the hosted child hub at
    /// <paramref name="address"/>, applying <paramref name="config"/> to its configuration when created.
    /// </summary>
    /// <param name="address">The address of the hosted hub.</param>
    /// <param name="config">Transform applied to the hosted hub's configuration when it is created.</param>
    /// <param name="create">Whether to create the hub if it does not yet exist.</param>
    /// <returns>The hosted hub, or <c>null</c> if it does not exist and <paramref name="create"/> is <see cref="HostedHubCreation.Never"/>.</returns>
    public IMessageHub? GetHostedHub(
        Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    ) => TryGetHostedHub(address, config, create).Hub;

    /// <summary>
    /// <see cref="GetHostedHub(Address, Func{MessageHubConfiguration, MessageHubConfiguration}, HostedHubCreation)"/>
    /// plus the reason a null hub came back — see <see cref="HostedHubOutcome"/>. Same work and
    /// same logging; this overload just does not throw the classification away
    /// (Systemorph/MeshWeaver#3243).
    /// </summary>
    /// <param name="address">The address of the hosted hub.</param>
    /// <param name="config">Transform applied to the hosted hub's configuration when it is created.</param>
    /// <param name="create">Whether to create the hub if it does not yet exist.</param>
    /// <returns>The hub (when there is one) and the outcome that produced this answer.</returns>
    public HostedHubResult TryGetHostedHub(
        Address address,
        Func<MessageHubConfiguration, MessageHubConfiguration> config,
        HostedHubCreation create
    )
    {
        if (create != HostedHubCreation.Never && !messageProcessingStarted)
            ReportHubConstructionDuringBuild(address);
        // 🚨 A pure read allocates nothing extra. HierarchicalRouting probes this per stream
        // message per parent-chain level with HostedHubCreation.Never, and that path was twice a
        // measured CPU hot frame; wrapping `config` unconditionally would put one closure per
        // probe on it for a transform that is only ever INVOKED when a hub is constructed.
        if (create == HostedHubCreation.Never)
            return hostedHubs.GetHubWithOutcome(address, config, create);
        // 🚨 Stamp the enclosing rung of the initialization ladder (HubInitializationBudget) — but
        // ONLY while this hub's own initialization is still running, because that is exactly when
        // one of its rung-2 waits can be waiting on the hub created here (a data source's stream
        // is served by a sync/{clientId} sub-hub built inside StartDataSourcesAndOpenGate). Then
        // the new hub must give up strictly sooner, or the level nearest a hang is torn down
        // before it can report it (#1122, #1186, #2886).
        //
        // 🚨 HOSTED is not the same as ENCLOSED, and reading it as the same is wrong in the case
        // that matters most: a per-node hub IS a hosted hub of the mesh root (MessageHubGrain and
        // MeshExtensions both create it through this method), but routing activates it on demand
        // long after the mesh hub reached Started — nothing in the mesh hub's initialization is
        // waiting on it, so contracting it would narrow a production bound for no reason at all.
        // RunLevel is the discriminator: below Started this hub's gates are still shut and its
        // init can still be in flight; at or above it, its initialization is over.
        //
        // Applied AFTER the caller's transform so an explicit WithStartupTimeout still decides its
        // own hub's rung 1, and here rather than in HostedHubsCollection because the host is an
        // Address there, not a hub whose budget can be read.
        if (RunLevel >= MessageHubRunLevel.Started)
            return hostedHubs.GetHubWithOutcome(address, config, create);
        return hostedHubs.GetHubWithOutcome(
            address,
            c => config(c) with { EnclosingInitializationBudget = Configuration.NestedInitializationBudget },
            create);
    }

    /// <summary>
    /// Set by <see cref="StartMessageProcessing"/>, the LAST thing
    /// <c>MessageHubConfiguration.Build</c> does. Until then this hub is still inside its own
    /// <c>Build</c>: its <c>SyncBuildupActions</c> are running and nothing it constructs can have
    /// been reached from a message.
    /// </summary>
    private bool messageProcessingStarted;

    private ImmutableList<Address> hubsConstructedDuringBuild = ImmutableList<Address>.Empty;

    /// <summary>
    /// The addresses of hubs this hub constructed from inside its own <c>Build</c> — the #1868
    /// invariant's evidence, recorded on the instance rather than only logged so a test can assert
    /// on it without depending on log plumbing. Empty is the invariant holding.
    /// </summary>
    public IReadOnlyList<Address> HubsConstructedDuringBuild => hubsConstructedDuringBuild;

    /// <summary>
    /// 🚨 <b>The invariant: <c>Build</c> must not construct another hub</b> (#1868).
    ///
    /// <para><b>The fact.</b> <c>MessageHubConfiguration.Build</c> runs <c>SyncBuildupActions</c>
    /// inline, before <see cref="StartMessageProcessing"/>. Two of those actions reached code that
    /// creates hubs — <c>DataExtensions.GetDefaultConfiguration</c>'s <c>h.GetWorkspace()</c>
    /// (→ <c>Workspace..ctor</c> → <c>DataContext.Initialize</c> → <c>DataSource.GetStream</c> →
    /// <c>SynchronizationStream..ctor</c>) and <c>KernelContainer</c>'s
    /// <c>StartActivityControlPlane</c> (→ <c>WatchControlPlane</c> → <c>AcquireStream</c> →
    /// <c>Workspace.GetStream</c>) — and <c>SynchronizationStream</c>'s constructor ALWAYS calls
    /// <c>GetHostedHub(…, HostedHubCreation.Always)</c>. So every data-enabled hub built at least
    /// one <c>sync/{clientId}</c> sub-hub, and a second Autofac container, inside its own
    /// <c>Build</c>. Measured with a depth counter on a GREEN run of
    /// <c>MeshWeaver.FutuRe.Test</c>: <b>1,350 nested Builds</b>, all depth=2.</para>
    ///
    /// <para><b>Why it matters</b> — and explicitly NOT as a crash claim, which #1867 withdrew and
    /// 1,350 nestings per green run refute. <i>A disposal that races a construction races a TREE of
    /// them.</i> That is the shape behind the whole shutdown-race family (#645, #715, #967, #1573),
    /// each of which had to widen its guard to cover work started by a construction that had itself
    /// been started by a construction. <c>HostedHubsCollection</c>'s in-flight counter tracks the
    /// OUTER creation; the inner one it spawns is a second entry, on the same thread, whose
    /// refusal/finish semantics are only correct because the guards were extended by hand, one
    /// incident at a time. It also makes <c>Build</c> reachable from arbitrary reactive emissions —
    /// the 08-18 dump has one nested <c>Build</c> reached from <c>MessageService.DrainOne</c> and
    /// one from a <c>MeshQuery</c> emission.</para>
    ///
    /// <para><b>Reported, not thrown.</b> The invariant is not yet universally true: this reports
    /// the two adopted sites' regressions and any new one, without turning an unadopted third-party
    /// configurator into a hard failure. Step 2 of the adoption — stopping
    /// <c>SynchronizationStream</c>'s constructor from creating its sub-hub eagerly — is what makes
    /// throwing here viable; it is deliberately not attempted with ~96 sites dereferencing
    /// <c>ISynchronizationStream.Hub</c> as non-null.</para>
    /// </summary>
    /// <param name="inner">Address of the hub whose construction was requested.</param>
    private void ReportHubConstructionDuringBuild(Address inner)
    {
        ImmutableInterlocked.Update(ref hubsConstructedDuringBuild, l => l.Add(inner));
        TryLog(LogLevel.Error,
            "[BUILD-NESTING] Hub {Outer} constructed hub {Inner} from inside its own Build — a "
            + "SyncBuildupAction reached hub construction, so a disposal racing {Outer}'s creation "
            + "races a TREE of constructions rather than one frame (#1868). Move the initialization "
            + "to the OBSERVABLE WithInitialization overload, which runs on InitializeHubRequest "
            + "after Build has returned.",
            Address, inner);
    }

    /// <summary>
    /// Couples a synchronous cleanup to the hub's lifetime by adding it to the hub's composite
    /// disposable; disposed during the ShutDown phase. A registrant added after disposal has begun
    /// is disposed immediately, so late registrations never leak.
    /// </summary>
    /// <param name="disposable">The resource to dispose when the hub shuts down.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(IDisposable disposable)
    {
        // Normal subscription logic: hold the IDisposable in the hub's
        // CompositeDisposable. If disposal has already started the composite is
        // disposed and Add disposes the registrant immediately — late registrants
        // never leak. No bag, no imperative drain.
        // Wrapped so a registrant that throws is named rather than truncating the
        // whole teardown walk — see GuardRegistrant. Applies to the late-registrant
        // path above too, which disposes through the same wrapper.
        disposables.Add(GuardRegistrant(disposable));
        return this;
    }

    /// <summary>
    /// Couples a synchronous cleanup callback (receiving this hub) to the hub's lifetime; runs during
    /// the ShutDown phase. Implemented by wrapping the callback in a disposable.
    /// </summary>
    /// <param name="disposeAction">The cleanup to run at shutdown, receiving this hub.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(Action<IMessageHub> disposeAction)
        => RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() => disposeAction(this)));

    /// <summary>
    /// Couples a synchronous cleanup to the hub's lifetime until the returned handle is disposed,
    /// which DETACHES it — removes it from the composite without disposing it. See
    /// <see cref="IMessageHub.RegisterForDisposalDetachable"/> and <see cref="DisposalRegistrantCount"/>.
    /// </summary>
    /// <param name="disposable">The resource to dispose when the hub shuts down, unless detached first.</param>
    /// <returns>A handle whose disposal detaches the registrant without disposing it.</returns>
    public IDisposable RegisterForDisposalDetachable(IDisposable disposable)
    {
        var entry = new DetachableRegistrant(GuardRegistrant(disposable));
        // A hub already disposing disposes the entry on Add (late registrants never leak), after
        // which Detach() reports false and the handle does nothing.
        disposables.Add(entry);
        return System.Reactive.Disposables.Disposable.Create(() =>
        {
            // Claim first, THEN remove: CompositeDisposable.Remove disposes what it removes, and the
            // claim is what makes that disposal a no-op. A teardown racing the detach claims the
            // same flag, so the registrant runs at most once and only if the teardown won.
            if (entry.Detach())
                disposables.Remove(entry);
        });
    }

    /// <summary>
    /// A registrant that runs its cleanup at most once, and not at all once detached. The single
    /// flag is shared by <see cref="Dispose"/> (the hub's teardown) and <see cref="Detach"/> (the
    /// registrant's subject ended), so whichever comes first decides.
    /// </summary>
    private sealed class DetachableRegistrant(IDisposable inner) : IDisposable
    {
        private int settled;

        /// <summary>Claims the registrant without running it. False when the teardown got there first.</summary>
        public bool Detach() => Interlocked.Exchange(ref settled, 1) == 0;

        /// <summary>Runs the cleanup unless it was detached (or already ran).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref settled, 1) == 0)
                inner.Dispose();
        }
    }

    /// <summary>
    /// Registers a reactive cleanup returning <see cref="IObservable{T}"/> (Unit) for I/O-performing
    /// teardown. Held in an immutable list, composed into one chain at dispose and subscribed so its
    /// async leaves run on the mesh IO pool.
    /// </summary>
    /// <param name="disposeAction">The reactive cleanup, receiving this hub and returning a Unit observable.</param>
    /// <returns>This hub, for chaining.</returns>
    public IMessageHub RegisterForDisposal(Func<IMessageHub, IObservable<Unit>> disposeAction)
    {
        // Reactive dispose actions are kept in an immutable list (NOT a ConcurrentBag)
        // and composed into one observable chain at dispose. Each returns
        // IObservable<Unit> precisely so it can be chained here and its async leaves
        // run on the mesh IO pool — see DisposeImpl.
        lock (reactiveDisposeLock)
            reactiveDisposeActions = reactiveDisposeActions.Add(disposeAction);
        return this;
    }

    /// <summary>The JSON serialization options used for messages on this hub (built from the parent hub's options).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>
    /// <c>true</c> from the moment <see cref="Dispose"/> begins. The reactive, Task-free
    /// "is this hub shutting down?" probe; observe <see cref="DisposalCompleted"/> for completion.
    /// </summary>
    public bool IsDisposing => disposalStarted;
    private volatile bool disposalStarted;

    /// <summary>
    /// How many synchronous cleanups are currently registered on this hub
    /// (<see cref="RegisterForDisposal(IDisposable)"/>) — i.e. the size of the composite that
    /// <c>DisposeImpl</c> walks in the ShutDown phase. Zero once the hub is down.
    ///
    /// <para>🚨 <b>This is a RETENTION reading, not a tidiness one.</b> The composite is
    /// append-only: <c>CompositeDisposable.Add</c> never prunes, and nothing else removes an
    /// entry, so every registrant a hub is handed is held — with everything its closure captured
    /// — for the hub's whole life. A registrant whose own subject is shorter-lived than the hub
    /// (a per-request watcher, a per-stream subscription) is therefore a monotone root, and this
    /// count is the only thing that can SEE it: a hub whose registrant count climbs with the
    /// traffic it has served is retaining one object graph per unit of that traffic.</para>
    ///
    /// <para>A hub with a bounded set of registrants — the ordinary case, wired once at
    /// construction — reports a small number that never moves.</para>
    /// </summary>
    public int DisposalRegistrantCount => disposables.Count;

    /// <summary>
    /// True when this hub is part of a shutdown: its own <see cref="Dispose"/> has begun, OR an
    /// ANCESTOR's disposal has frozen hosted-hub creation across the subtree
    /// (<see cref="HostedHubsCollection.CloseCreation"/> cascades at the first instant of the
    /// ancestor's <c>Dispose()</c>, strictly BEFORE this hub's own <see cref="IsDisposing"/>
    /// flips — the <c>DisposeRequest</c> only reaches it in the ancestor's DisposeHostedHubs
    /// phase). A frozen subtree means disposal of this hub is already in progress or imminent,
    /// so anything that terminates because of it is a recognized shutdown outcome, not a fault.
    /// </summary>
    public bool IsShuttingDown => disposalStarted || hostedHubs.IsCreationFrozen;

    /// <summary>
    /// Backing source for <see cref="ShuttingDown"/>. An AsyncSubject so a subscriber attaching
    /// after the moment still receives it — the same replay contract as
    /// <see cref="disposalCompleted"/>, at the OTHER end of teardown. Completed exactly once
    /// (CAS-guarded) by <see cref="SignalShuttingDown"/>, from whichever of the two entry points
    /// — this hub's own <see cref="Dispose"/> or an ancestor's <see cref="CloseHostedHubCreation"/>
    /// cascade — fires first.
    /// </summary>
    private readonly AsyncSubject<Unit> shuttingDown = new();
    private int shuttingDownSignalled;

    /// <inheritdoc />
    public IObservable<Unit> ShuttingDown => shuttingDown.AsObservable();

    /// <summary>
    /// Completes <see cref="shuttingDown"/> exactly once. Idempotent — both entry points can run
    /// for one hub (an ancestor freezes the subtree, then this hub's own Dispose arrives seconds
    /// later) and only the first is the "first instant". Subscribers run INLINE on the caller's
    /// thread (the disposing ancestor's <c>Dispose()</c>), so the notification is guarded: a
    /// teardown signal must never fault the teardown it announces.
    /// </summary>
    private void SignalShuttingDown()
    {
        if (Interlocked.CompareExchange(ref shuttingDownSignalled, 1, 0) != 0)
            return;
        try
        {
            shuttingDown.OnNext(Unit.Default);
            shuttingDown.OnCompleted();
        }
        catch (Exception ex)
        {
            TryLog(LogLevel.Warning, ex,
                "[SHUTTING-DOWN] {Address}: a ShuttingDown subscriber threw — teardown proceeds", Address);
        }
    }

    /// <inheritdoc />
    // Native reactive view of the completion subject — NOT bridged from a Task. Fires Unit +
    // completes when disposal finishes (or OnError on a disposal fault); a subscriber attaching
    // AFTER completion still observes the terminal notification immediately (ReplaySubject(1)).
    public IObservable<Unit> DisposalCompleted => disposalCompleted.AsObservable();

    /// <summary>
    /// Every disposal-PROGRESS signal from this hub and its hosted SUBTREE: each hub's
    /// <c>RunLevel</c> transition, recursively. Not a heartbeat — nothing here is emitted on a
    /// timer, so a stream that goes quiet means the teardown genuinely stopped moving.
    ///
    /// <para>Consumed by the disposal watchdog, which re-arms on every signal (see
    /// <see cref="Dispose"/>). Exposed so a test can assert the difference between "slow but
    /// progressing" and "wedged" without waiting on wall-clock.</para>
    /// </summary>
    public IObservable<string> DisposalProgress => DisposalProgressAtDepth(0);

    /// <summary>
    /// <see cref="DisposalProgress"/> with the recursion depth threaded through, so the subtree
    /// walk is capped exactly like the diagnostics snapshot.
    /// </summary>
    /// <param name="depth">Current recursion depth.</param>
    /// <returns>A hot stream of progress descriptions; never faults.</returns>
    internal IObservable<string> DisposalProgressAtDepth(int depth)
    {
        // RunLevelChanged is a BehaviorSubject, so subscribing reports the CURRENT level at once —
        // that first emission is the "we are observing from here" mark, and it costs nothing
        // because the watchdog re-arms on it exactly as it would on a transition.
        var own = runLevelChanged.Select(level => $"{Address} → {level}");
        return depth >= MaxHostedHubRecursionDepth
            ? own
            : own.Merge(hostedHubs.SubtreeDisposalProgress(depth + 1));
    }

    /// <summary>
    /// Set when the Quiescing-phase drain budget (<see cref="QuiesceTimeout"/>)
    /// expires with callbacks still pending. Tests inspect this on the root mesh
    /// (and recursively on hosted hubs via <see cref="GetDisposalDiagnostics"/>)
    /// to fail loud rather than silently swallow leaked Observe subscriptions.
    /// </summary>
    public bool QuiescingTimedOut { get; private set; }
    /// <summary>One-line summary of the pending callbacks at the moment Quiescing
    /// timed out — empty if it didn't fire. Used by the test-base error message.</summary>
    public string? QuiescingTimeoutDetail { get; private set; }

    // Reactive disposal-completion source of truth. ReplaySubject(1): completed exactly once
    // (guarded by `disposalSignalled` CAS) via SignalDisposalCompleted / SignalDisposalFaulted.
    private readonly ReplaySubject<Unit> disposalCompleted = new(1);
    private int disposalSignalled;
    // Disposal-phase Rx subscriptions, held so the scheduler keeps them rooted; each self-
    // completes (Take(1)/Timeout/TakeUntil) and is also disposed in the ShutDown finally.
    private IDisposable? watchdogSubscription;
    private IDisposable? quiescingSubscription;
    private IDisposable? hostedHubsDisposalSubscription;
    /// <summary>
    /// The STALL budget of the disposal watchdog: how long the subtree may make no
    /// <see cref="RunLevel"/> progress before the watchdog looks at what is holding the
    /// teardown. It is not a duration cap and it forces nothing — see
    /// <see cref="OnDisposalStall"/> for the verdicts it can reach.
    /// </summary>
    internal static readonly TimeSpan DisposalWatchdogTimeout = TimeSpan.FromSeconds(8);
    // Stall-detector state (see OnDisposalStall). `stallTurnsSeen` is the pump's completed-turn
    // count at the last stall verdict; `wedgedTurnCancelled` records that the in-flight turn has
    // already been handed its cancellation, so the next stall verdict on the same turn is the
    // Error that names a handler ignoring cancellation, not a second cancel.
    private long stallTurnsSeen = -1;
    // The pump's DEQUEUE count at the last stall verdict, and the one it carried when Dispose() was
    // called. Two baselines because they answer two different questions and the verdict quotes
    // both: "nothing was dequeued in the last budget" is the predicate, "nothing has been dequeued
    // at all since Dispose()" is the stronger fact when it holds. Neither is derivable from
    // `stallTurnsSeen`, which counts HANDLER completions (#3593).
    private long stallDequeuedSeen = -1;
    private long disposalDequeuedBaseline = -1;
    private bool wedgedTurnCancelled;
    // Event ids for the stall verdicts: the red-log triage keys an incident on the log SITE and
    // deliberately ignores prose, so each verdict shape files as ONE issue however many hubs
    // reach it, and the prose is free to carry every detail a reproduction needs.
    internal static readonly EventId DisposalWedgedTurnCancelled = new(7311, nameof(DisposalWedgedTurnCancelled));
    internal static readonly EventId DisposalWedgedTurnIgnoresCancellation = new(7312, nameof(DisposalWedgedTurnIgnoresCancellation));
    internal static readonly EventId DisposalStalledBelowThisHub = new(7313, nameof(DisposalStalledBelowThisHub));
    internal static readonly EventId DisposalShutDownPhaseBlocked = new(7314, nameof(DisposalShutDownPhaseBlocked));
    internal static readonly EventId DisposalQuiesceWaitCutOff = new(7315, nameof(DisposalQuiesceWaitCutOff));
    internal static readonly EventId DisposalPumpNeverDequeued = new(7316, nameof(DisposalPumpNeverDequeued));
    internal static readonly EventId DisposalStalledUnclassified = new(7317, nameof(DisposalStalledUnclassified));
    // 7318 — a ShutdownRequest turn stalled BELOW the ShutDown phase, where no registrant has run.
    // Its own event so it files as its own incident: 7314's finding is a blocking registrant and
    // this one's is a phase transition that was never made, which are different investigations
    // (#3593). Sharing 7314 would fold both onto one issue titled after the wrong one.
    internal static readonly EventId DisposalPhaseBelowShutDownBlocked =
        new(7318, nameof(DisposalPhaseBelowShutDownBlocked));
    private readonly Stopwatch disposalStopwatch = new();

    private bool DisposalSignalled => Volatile.Read(ref disposalSignalled) != 0;

    /// <summary>Completes <see cref="disposalCompleted"/> exactly once (idempotent CAS).
    /// Reactive replacement for the old <c>disposingTaskCompletionSource.TrySetResult()</c>.</summary>
    private void SignalDisposalCompleted()
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnNext(Unit.Default);
        disposalCompleted.OnCompleted();
    }

    /// <summary>Faults <see cref="disposalCompleted"/> exactly once (idempotent CAS).
    /// Reactive replacement for the old <c>disposingTaskCompletionSource.TrySetException(e)</c>.</summary>
    private void SignalDisposalFaulted(Exception error)
    {
        if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0)
            return;
        disposalCompleted.OnError(error);
    }

    private readonly Lock locker = new();

    /// <summary>
    /// Begins the hub's reactive, phased teardown (idempotent). Freezes hosted-hub creation, posts
    /// the Quiescing shutdown request that drives the Quiescing → DisposeHostedHubs → ShutDown state
    /// machine behind whatever work the hub has already accepted, and arms a stall detector that
    /// reports — and cooperatively cancels — a turn that stops the path from advancing. Nothing is
    /// forced: a hub that cannot finish stays pending and says why. Returns immediately; observe
    /// <see cref="DisposalCompleted"/> for completion.
    /// </summary>
    /// <summary>
    /// Freezes creation of NEW hosted hubs beneath this hub, cascading through the
    /// whole subtree. Invoked by the parent's <see cref="HostedHubsCollection.CloseCreation"/>
    /// the moment an ANCESTOR begins disposing, so no straggler emission anywhere in
    /// the tree can enter hub construction during teardown (issue #613). Does NOT
    /// dispose anything — existing hubs keep draining until their own dispose phase.
    ///
    /// <para>It IS, however, the first instant this hub is part of a shutdown
    /// (<see cref="IsShuttingDown"/> flips here), so it also raises <see cref="ShuttingDown"/> —
    /// the watchers this hub owns stop NOW, not when its own ShutDown phase disposes their
    /// registrations seconds later (#3026).</para>
    /// </summary>
    internal void CloseHostedHubCreation()
    {
        hostedHubs.CloseCreation();
        SignalShuttingDown();
    }

    public void Dispose()
    {
        // 🚨 THE GOODBYE GOES HERE, NOT ON THE ROUTED REQUEST (#3986) — and the difference is an
        // ORLEANS DEACTIVATION.
        //
        // This is the FIRST statement of the teardown, so it runs while the hub is whole: IsDisposing
        // is still false, the workspace's client-subscription registry is intact, and the carrier that
        // will deliver after DisposalCompleted is still resolvable. Nothing here posts — see
        // Workspace.AnnounceRecycleToClientSubscriptions.
        AnnounceRecycleUnlessAnAncestorIsTakingUsWithIt();

        var totalStopwatch = Stopwatch.StartNew();
        lock (locker)
        {
            if (IsDisposing)
            {
                logger.LogDebug("Dispose() called multiple times for hub {address} (elapsed: {elapsed}ms)", Address, totalStopwatch.ElapsedMilliseconds);
                return;
            }
            logger.LogDebug("STARTING DISPOSAL of hub {address}, current Version={Version}, hosted hubs count: {hostedHubsCount}",
                Address, Version, hostedHubs.Hubs.Count());

            disposalStopwatch.Start();
            disposalStarted = true;

        }

        // Close hosted-hub CREATION immediately — not only when the DisposeHostedHubs
        // phase disposes the collection. A hub created in the Quiescing window races
        // DisposeHubsReactive's snapshot and leaks as a zombie whose timers later
        // detonate on the disposed container (post-dispose ObjectDisposedException
        // stragglers). CloseCreation cascades the freeze through the ENTIRE subtree
        // (see HostedHubsCollection.CloseCreation — the FutuRe.Test teardown SIGSEGV,
        // issue #613). Existing hubs still resolve for the drain.
        hostedHubs.CloseCreation();
        // The first instant of THIS hub's teardown — every watcher it owns stops here, before the
        // Quiescing phase starts waiting for the callbacks those watchers would otherwise keep
        // issuing (#3026). Descendants were signalled by the cascade above.
        SignalShuttingDown();

        // Log all hosted hubs that will be disposed
        var hostedHubAddresses = hostedHubs.Hubs.Select(h => h.Address.ToString()).ToArray();
        if (hostedHubAddresses.Length > 0)
        {
            logger.LogDebug("Hub {address} has {count} hosted hubs to dispose: [{hubAddresses}]",
                Address, hostedHubAddresses.Length, string.Join(", ", hostedHubAddresses));
        }
        else
        {
            logger.LogDebug("Hub {address} has no hosted hubs to dispose", Address);
        }

        // The stall detector's baseline: how many turns the pump had completed when the teardown
        // began. Its first verdict compares against THIS, so a pump that drained 500 accepted turns
        // in the first budget reads as busy rather than as a wedge with no history.
        stallTurnsSeen = (messageService as MessageService)?.TurnsCompleted ?? -1;
        // The DEQUEUE baseline, taken in the same breath. The completed-turn baseline above cannot
        // see a pump that never took anything off its queue — both counters read the same frozen
        // value then, and the detector attributed the stall to a child that had not been asked to
        // do anything yet (#3593, 47 hubs in one shutdown).
        stallDequeuedSeen = disposalDequeuedBaseline =
            (messageService as MessageService)?.TurnsDequeued ?? -1;

        // 🚨 The in-flight turn is NOT cancelled here. Work this hub accepted before teardown began
        // runs to completion — a merge that is half-way through, a handler awaiting pooled I/O.
        // The ShutdownRequest posted below queues behind it FIFO, so the shutdown proceeds the
        // moment the turn returns. This used to call messageService.CancelExecution() on entry,
        // which aborted whatever the hub was doing at the instant an ancestor decided to tear
        // down — a cooperative handler then unwound with its work undone. Cancellation is now a
        // VERDICT the stall detector reaches after the turn has provably stopped making progress
        // (see OnDisposalStall), never a reflex.
        //
        // The ONE exception is a hub that never finished STARTING. Its InitializeHubRequest is
        // the hub's own bring-up, not work anyone handed it — intake is gated behind it, so no
        // accepted turn can be waiting on its result — and a bring-up that is still running when
        // the owner tears down produces nothing the owner will keep. Cancelling it now is what
        // lets a hung initialization (a data source that never answers, a NodeType that never
        // compiles) release the block at once instead of holding its whole ancestry pending for
        // a stall budget. Anything parked behind its gates is answered ShuttingDown and reported
        // by messageService.Dispose (DISPOSE-DISCARD).
        if (RunLevel < MessageHubRunLevel.Started)
            messageService.CancelExecution();

        logger.LogDebug("POSTING initial ShutdownRequest for hub {Address} with Version={Version} (disposal preparation took {elapsed}ms)",
            Address, Version, totalStopwatch.ElapsedMilliseconds);
        Post(new ShutdownRequest(MessageHubRunLevel.Quiescing, Version));

        // The disposal STALL DETECTOR. It watches the teardown; it never performs it.
        //
        // 🚨 Reactive, NOT a Task.Delay. `Observable.Timer` schedules on the DefaultScheduler
        // (OFF the action block), and `TakeUntil(disposalCompleted)` cancels the timer the
        // instant disposal finishes — so a normal fast disposal releases the TimerQueue entry
        // immediately. (The original uncancelled `Task.Delay(25s)` rooted the ENTIRE hub graph
        // — cache, data sources, action block, subscriptions — for 25 s after EVERY dispose,
        // even a fast one: TimerQueue → TimerQueueTimer → DelayPromise → state machine → hub.)
        //
        // 🚨 It measures a STALL, not a DURATION (#1701). A fixed-duration watchdog on an owner
        // OUT-RUNS the very mechanism that answers it: the owner arms at its own Dispose(), a
        // child arms strictly later — in the owner's DisposeHostedHubs phase — for the same 8 s.
        // Switch() over the progress stream re-arms the timer on every sign of life anywhere in
        // the subtree, so a healthy nested teardown never trips however deep it is, and a hub that
        // genuinely stops moving is looked at 8 s later — and again every 8 s while it stays
        // still, because the periodic Timer keeps ticking until progress resumes or disposal ends.
        //
        // 🚨 It FORCES NOTHING. The predecessor ran an out-of-band teardown from this thread —
        // hosted hubs, callbacks, dispose actions, message service — while the wedged turn was
        // still executing, then signalled Dead. Measured, that produced a hub that reported itself
        // disposed while its children were mid-flight, a NACK minted only after the 8 s force had
        // fired (the waiter's whole budget spent on a verdict that existed), and in production a
        // pod whose "completed" teardown was still holding work. A hub that cannot finish its
        // teardown now SAYS SO, names the turn, cancels it once (cooperatively), and stays
        // Pending: the outer bounds — the test base's dispose deadline, the host's teardown
        // budget — report a hang with these diagnostics attached instead of a lie about
        // completion. See OnDisposalStall for the verdicts, one of which is an explicit unknown.
        watchdogSubscription = DisposalProgress
            .StartWith($"{Address} Dispose() called")
            .Select(reason => Observable.Timer(DisposalWatchdogTimeout, DisposalWatchdogTimeout).Select(_ => reason))
            .Switch()
            .TakeUntil(disposalCompleted)
            // 🚨 OFF the progress path. Switch serialises its downstream delivery under its own
            // gate, and a progress signal originates INSIDE the emitting hub's `locker` (the
            // RunLevel setter runs under it in HandleShutdownCore). Running the verdict inline
            // would therefore hold Switch's gate while taking a child's `locker`, while that
            // child holds its `locker` and wants Switch's gate — a lock-order inversion that
            // wedges the child's action block on its own ShutdownRequest (measured: the child
            // sitting at Executing(ShutdownRequest, 8000ms) with the drain latched — printed at the
            // time as deliveryActionCompleted=False, which is `draining=True` in today's snapshot).
            .ObserveOn(System.Reactive.Concurrency.DefaultScheduler.Instance)
            .Subscribe(
                OnDisposalStall,
                // disposalCompleted faulting (SignalDisposalFaulted) propagates through TakeUntil;
                // the watchdog is no longer needed — swallow so it isn't an unobserved error.
                _ => { });
    }

    /// <summary>
    /// One stall verdict: the subtree has made no <see cref="RunLevel"/> progress for
    /// <see cref="DisposalWatchdogTimeout"/>. Reached every budget while the stall persists.
    ///
    /// <para><b>Busy is not wedged.</b> The RunLevel only moves when a ShutdownRequest is
    /// processed, and that request queues FIFO behind whatever this hub had already accepted. A
    /// pump that keeps completing turns is draining accepted work ahead of its shutdown — the
    /// designed behaviour, reported at Information and left alone.</para>
    ///
    /// <para><b>A wedged turn is cancelled once, then named.</b> A turn that has held the block
    /// for a whole budget without a single completion is handed its cancellation token — the one
    /// cooperative kill the actor model has. A handler that observes it returns and the shutdown
    /// proceeds. One that does not is reported at Error on every following budget, with the
    /// message type and its age, as the defect it is. Nothing is torn down around it.</para>
    ///
    /// <para><b>No turn, no progress, and the pump has not turned</b> is THIS hub's own stall: the
    /// drain flag is latched and nothing has come off the queue for a whole budget, so the queued
    /// work — the ShutdownRequest included — was never handed to the pipeline at all. Nothing below
    /// has been asked to do anything, so the verdict names the pump and the scheduler that owes it
    /// a turn.</para>
    ///
    /// <para><b>No turn, no progress, but the pump HAS turned</b> means the stall is below this hub
    /// — a hosted hub that is itself wedged, or a join still waiting on one. The recursive
    /// diagnostics say which; that child's own detector carries the turn-level verdict. 🚨 This
    /// verdict is only reachable when there IS something below: hosted hubs, or a teardown that has
    /// reached the child-disposal phase / holds the hosted-hub join. It used to be the unguarded
    /// fallback, so it asserted a cause the snapshot could not support — on 2026-09-06 it sent 47
    /// readers to children of hubs still at <c>RunLevel=Started</c>, which have no children in
    /// flight by construction (#3593).</para>
    ///
    /// <para><b>Anything else</b> is reported as an explicit UNKNOWN naming what was and was not
    /// observed. A verdict that guesses is worse than one that says it cannot tell.</para>
    /// </summary>
    /// <param name="lastProgress">The last progress signal seen before the stall.</param>
    private void OnDisposalStall(string lastProgress)
    {
        if (DisposalSignalled)
            return;

        var pump = messageService as MessageService;
        var turns = pump?.TurnsCompleted ?? -1;
        var dequeued = pump?.TurnsDequeued ?? -1;
        var snapshot = pump?.GetQueueSnapshot();

        if (pump is not null && turns != stallTurnsSeen)
        {
            var completed = turns - stallTurnsSeen;
            stallTurnsSeen = turns;
            stallDequeuedSeen = dequeued;
            // Turns are completing: the pump is busy, not wedged. A new turn means the previous
            // one returned, so a cancellation issued for it is spent — start the next one clean.
            wedgedTurnCancelled = false;
            TryLog(LogLevel.Information,
                "[DISPOSE-BUSY] {Address}: no phase transition for {Timeout}, but the pump completed "
                + "{Turns} turn(s) in that window (queue depth {Depth}) — accepted work is draining ahead "
                + "of the shutdown request. Not a wedge; waiting.",
                Address, DisposalWatchdogTimeout, completed, snapshot?.Buffer ?? -1);
            return;
        }
        stallTurnsSeen = turns;
        // Captured BEFORE the baseline moves: did anything at all come off the queue in the budget
        // just ended? Every branch below is downstream of this reading, so it is taken once.
        var nothingDequeuedThisBudget = pump is not null && dequeued == stallDequeuedSeen;
        stallDequeuedSeen = dequeued;

        if (snapshot?.CurrentMessage is { } young
            && snapshot.Value.CurrentMessageElapsedMs < DisposalWatchdogTimeout.TotalMilliseconds)
        {
            // A turn younger than the budget started AFTER the last look — the pump is moving even
            // if the counter has not caught up with this verdict; nothing here is wedged yet.
            TryLog(LogLevel.Information,
                "[DISPOSE-BUSY] {Address}: no phase transition for {Timeout}, but the turn on the block "
                + "({MessageType}) is only {ElapsedMs}ms old — the pump is moving; waiting.",
                Address, DisposalWatchdogTimeout, young, snapshot.Value.CurrentMessageElapsedMs);
            return;
        }

        if (snapshot?.CurrentMessage is { } current)
        {
            if (current == nameof(ShutdownRequest))
            {
                // The turn on the block IS a shutdown phase. Which phase decides what the finding
                // is, and this verdict used to assert the ShutDown one unconditionally.
                //
                // 🚨 ONLY the ShutDown phase walks the registrants. `DisposeImpl` →
                // disposables.Dispose and messageService.Dispose are reached exclusively from
                // `case MessageHubRunLevel.ShutDown:` in HandleShutdownCore — so at Quiescing or
                // DisposeHostedHubs the hub has not touched `disposables` at all, and "a registered
                // cleanup is BLOCKING inside DisposeImpl" is false BY CONSTRUCTION. This is the
                // same defect #3615 removed from the pump verdict (7313) and left in its sibling:
                // a verdict asserting a cause the snapshot cannot support. It sent this issue's
                // investigation through every disposal registrant of a `sync/*` hub — there are a
                // dozen, none of them reachable — for a hub the report itself showed at Quiescing
                // (#3593, measured 2026-09-19: `RunLevel=Quiescing … Executing(ShutdownRequest,
                // 253055ms)`).
                if (RunLevel >= MessageHubRunLevel.ShutDown)
                {
                    // Measured in production (memex-cloud, 2026-08-29 → 09-03): sync/* hubs reported
                    // `(last progress: sync/… → ShutDown). RunLevel=ShutDown` dozens of times per
                    // shutdown, and the predecessor then tore them down out of band after 8–23 s.
                    // It runs with CancellationToken.None by design, so there is nothing to cancel;
                    // the finding is the blocking registrant.
                    logger.LogError(DisposalShutDownPhaseBlocked,
                        "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
                        + "(last progress: {LastProgress}). RunLevel={RunLevel}. The ShutDown phase itself has "
                        + "held the action block for {ElapsedMs}ms — a registered cleanup is BLOCKING inside "
                        + "DisposeImpl or messageService.Dispose (a Dispose waiting on a lock, a teardown "
                        + "joining a turn it is itself occupying). Disposal is NOT forced; find the registrant.\n{Diagnostics}",
                        Address, DisposalWatchdogTimeout, lastProgress, RunLevel,
                        snapshot.Value.CurrentMessageElapsedMs, DescribeWedge());
                    return;
                }

                // Below ShutDown. No registrant has run, so the only work this turn does is the
                // phase transition itself.
                //
                // 🚨 The guidance is PER PHASE, because the two phases below ShutDown are bounded by
                // different things and a verdict that cites the wrong one is this issue's own defect
                // in miniature. Quiescing is bounded by QuiesceTimeout (default 2 s) ×
                // MaxQuiesceRearms (20) ≈ 42 s with every re-arm logging [QUIESCE-WAIT], so an
                // elapsed far past that with an idle pump means the transition was never made — and
                // the [QUIESCE-*] lines say which half. DisposeHostedHubs has no such ceiling: it
                // waits on the children, so the reading there is the recursive snapshot below.
                var belowShutDownGuidance = RunLevel == MessageHubRunLevel.Quiescing
                    ? $"Quiescing is bounded by QuiesceTimeout x {MaxQuiesceRearms} re-arms (~42s at the "
                      + "default), each logging [QUIESCE-WAIT], so an elapsed far past that with an idle "
                      + "pump means the transition was never made. Read this hub's [QUIESCE-START] / "
                      + "[QUIESCE-OK] / [QUIESCE-WAIT] / [QUIESCE-TIMEOUT] lines: a [QUIESCE-START] with "
                      + "none of the others means the quiesce wait never completed, while a [QUIESCE-OK] "
                      + "or [QUIESCE-TIMEOUT] means it did and the phase-advancing Post is what did not land"
                    : "this phase waits on the hosted hubs rather than on a budget, so the reading is the "
                      + "recursive snapshot below — the child that has not reached Dead is the finding, and "
                      + "its own detector carries the turn-level verdict";

                logger.LogError(DisposalPhaseBelowShutDownBlocked,
                    "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
                    + "(last progress: {LastProgress}). RunLevel={RunLevel} — BELOW ShutDown, so NO registered "
                    + "cleanup has run and none can be blocking: DisposeImpl and messageService.Dispose are "
                    + "reached only in the ShutDown phase. The ShutdownRequest turn has been on the block for "
                    + "{ElapsedMs}ms. The finding is the phase TRANSITION, not a registrant: {Guidance}. "
                    + "Disposal is NOT forced.\n{Diagnostics}",
                    Address, DisposalWatchdogTimeout, lastProgress, RunLevel,
                    snapshot.Value.CurrentMessageElapsedMs, belowShutDownGuidance, DescribeWedge());
                return;
            }
            if (!wedgedTurnCancelled)
            {
                wedgedTurnCancelled = true;
                // 🚨 An ERROR, not a warning: having to cancel a turn means the hub did not finish
                // the work it accepted, and the red-log pipeline files an Error as an issue. The
                // line carries what a reproduction needs — the hub, the message type, how long it
                // has held the block, the queue behind it and the recursive disposal snapshot.
                logger.LogError(DisposalWedgedTurnCancelled,
                    "[DISPOSE-WEDGE] Hub {Address}: the turn {MessageType} has held the action block for "
                    + "{ElapsedMs}ms with no progress (last progress: {LastProgress}); RunLevel={RunLevel}, "
                    + "queue depth {Depth}, {Turns} turn(s) completed since Dispose(). Cancelling its token so a "
                    + "cooperative handler returns and the shutdown behind it can proceed — the work of that "
                    + "turn did NOT finish. Find what it was waiting on; a turn that returns is never cancelled.\n{Diagnostics}",
                    Address, current, snapshot.Value.CurrentMessageElapsedMs, lastProgress, RunLevel,
                    snapshot.Value.Buffer, turns, DescribeWedge());
                messageService.CancelExecution();
                return;
            }
            logger.LogError(DisposalWedgedTurnIgnoresCancellation,
                "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
                + "(last progress: {LastProgress}). RunLevel={RunLevel}. The turn {MessageType} "
                + "({ElapsedMs}ms, queue depth {Depth}) was handed its cancellation and is still running — a "
                + "handler that ignores cancellation is a defect in that handler. Disposal is NOT forced: it "
                + "stays pending until the turn returns, and the outer teardown bound reports this hang.\n{Diagnostics}",
                Address, DisposalWatchdogTimeout, lastProgress, RunLevel, current,
                snapshot.Value.CurrentMessageElapsedMs, snapshot.Value.Buffer, DescribeWedge());
            return;
        }

        if (RunLevel == MessageHubRunLevel.Quiescing)
        {
            var owed = SnapshotPendingCallbacks()
                .Where(c => AReplyIsOwedByAShuttingDownLocalHub(c.Target)).ToArray();
            if (owed.Length > 0)
            {
                TryLog(LogLevel.Information,
                    "[DISPOSE-BUSY] {Address}: quiescing — {Count} reply(ies) still owed by shutting-down hub(s) "
                    + "in this mesh ({Pending}); their own teardown answers them. Waiting.",
                    Address, owed.Length, FormatPendingCallbacks(owed));
                return;
            }
        }

        // THE PUMP VERDICT. Queue non-empty, the drain flag latched, no turn dequeued for a whole
        // budget, and nothing on the block. Work is queued and a drain IS nominally in flight, yet
        // the queue has not moved — so the ShutdownRequest that Dispose() posted was never handed
        // to the pipeline, and nothing below this hub has been asked to do anything. Naming a child
        // here is a category error, which is precisely what the unguarded fallback below used to do
        // (#3593: 47 sync/* hubs, all at RunLevel=Started with queue depth 1).
        //
        // 🚨 The line used to say "a drain IS scheduled on this hub's TaskScheduler" and then send
        // the reader to that scheduler — an ASSERTION, not a measurement. Nothing in the snapshot
        // could tell "the scheduler accepted a drain and never ran it" from "the latch is set and
        // nothing is outstanding at all", and those have opposite owners. `drainsAwaiting` is that
        // measurement (#3593): scheduled minus started, read off the pump.
        //
        // The three states, in the order the line reports them:
        //   drainsInFlight > 0   → a drain body IS running and is blocked BEFORE the dequeue — in
        //                          the turn gate, or in whatever runs ahead of taking work off the
        //                          queue.
        //   drainsAwaiting > 0   → the turn scheduler ACCEPTED a drain and has not run it. The
        //                          cause is outside this hub: a genuinely saturated pool, or — for
        //                          a ROOT GRAIN hub only — an Orleans ActivationTaskScheduler that
        //                          stopped executing work.
        //                          🚨 The last one does NOT apply to a hosted hub: hosted hubs are
        //                          built from a fresh MessageHubConfiguration and inherit no
        //                          scheduler, which is what WithTaskScheduler's contract asks for.
        //                          🚨 And "parked on the POSTING thread's local LIFO queue" is no
        //                          longer one of the causes: ScheduleDrainOne now asks for
        //                          PreferFairness, so the drain goes to the pool's GLOBAL queue
        //                          where any worker — and the pool's own starvation detection —
        //                          can see it. That was #3593's mechanism, and reading this branch
        //                          on TaskScheduler.Default today means the pool really is out of
        //                          threads, not that the work is hiding on one of them.
        //   drainsAwaiting == 0  → NOTHING is outstanding while the latch is set. That breaks
        //                          ScheduleDrainOne's invariant and is a defect in the pump itself,
        //                          not in any scheduler. It is reachable only if a schedule was
        //                          lost without releasing the latch, which ScheduleDrainOne now
        //                          refuses to do — so if this branch is ever printed, it is new.
        if (snapshot is { } pumpView && pump is not null
            && pumpView.Draining && pumpView.Buffer > 0 && nothingDequeuedThisBudget)
        {
            var drainsAwaiting = pumpView.DrainsAwaitingScheduler;
            var mechanism = pumpView.DrainsInFlight > 0
                ? "a drain body IS running (drainsInFlight=" + pumpView.DrainsInFlight
                  + ") and is blocked BEFORE the dequeue — in the turn gate, or in whatever it does "
                  + "ahead of taking work off the queue"
                : drainsAwaiting > 0
                    ? "the turn scheduler ACCEPTED " + drainsAwaiting + " drain(s) and has not run "
                      + "them (drainsInFlight=0). The stall is in THAT SCHEDULER, not in this hub. "
                      + "On TaskScheduler.Default (every hosted hub — a hosted hub is built from a "
                      + "fresh configuration and inherits no scheduler) the drain is queued GLOBALLY "
                      + "(ScheduleDrainOne asks for PreferFairness since #3593), so it is visible to "
                      + "every worker AND to the pool's starvation detection — reading this here "
                      + "means the pool genuinely has no thread to give, NOT that the work is parked "
                      + "on the posting thread's local queue; on a ROOT GRAIN hub it is the grain's "
                      + "ActivationTaskScheduler (MessageHubGrain.WithTaskScheduler), where a wedged "
                      + "or deactivated activation parks every turn this hub will ever take"
                    : "NOTHING is outstanding (drainsInFlight=0, drainsScheduled==drainsStarted) "
                      + "while the drain flag is latched. That breaks the pump's own invariant — a "
                      + "latched flag must mean a drain is running or queued — so this is a defect "
                      + "in MessageService.ScheduleDrainOne, NOT in any scheduler";

            logger.LogError(DisposalPumpNeverDequeued,
                "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
                + "(last progress: {LastProgress}). RunLevel={RunLevel}, queue depth {Depth}. "
                + "THE PUMP IS NOT TURNING: the drain flag is latched, drainsInFlight={DrainsInFlight}, "
                + "drainsAwaitingScheduler={DrainsAwaiting}, and NO turn was dequeued in that window "
                + "({Dequeued} dequeued in total since Dispose()). The queued work — the "
                + "ShutdownRequest included — has therefore never been handed to a handler, so this "
                + "stall is in THIS hub's turn scheduling and NOT in a hosted hub or a join. "
                + "MECHANISM: {Mechanism}. Disposal is NOT forced.\n{Diagnostics}",
                Address, DisposalWatchdogTimeout, lastProgress, RunLevel, pumpView.Buffer,
                pumpView.DrainsInFlight, drainsAwaiting, dequeued - disposalDequeuedBaseline,
                mechanism, DescribeWedge());
            return;
        }

        // 🚨 GUARDED. "The stall is below this hub" is only sayable when there IS something below:
        // hosted hubs still in the collection, or a teardown that has reached the child-disposal
        // phase (where the collection may already be empty while its join is outstanding).
        var hosted = hostedHubs.Hubs.ToArray();
        var somethingBelow = hosted.Length > 0
                             || hostedHubsDisposalSubscription is not null
                             || RunLevel >= MessageHubRunLevel.DisposeHostedHubs;
        if (somethingBelow)
        {
            logger.LogError(DisposalStalledBelowThisHub,
                "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
                + "(last progress: {LastProgress}). RunLevel={RunLevel}, queue depth {Depth}. No turn is "
                + "executing on this hub and its pump has dequeued {Dequeued} turn(s) since Dispose(), "
                + "and it has {Hosted} hosted hub(s) below it, so the stall is in a hosted hub or a join "
                + "it is waiting on — the diagnostics below name it. Disposal is NOT forced.\n{Diagnostics}",
                Address, DisposalWatchdogTimeout, lastProgress, RunLevel, snapshot?.Buffer ?? -1,
                dequeued - disposalDequeuedBaseline, hosted.Length, DescribeWedge());
            return;
        }

        // An explicit UNKNOWN. Nothing is on the block, the pump is not latched-and-frozen, and
        // there is nothing below — so none of the named causes fits, and the honest report is the
        // measurement plus the statement that it does not identify a cause. A fallback that picks
        // the nearest bucket teaches every reader to trust a verdict it did not earn.
        logger.LogError(DisposalStalledUnclassified,
            "DISPOSAL DEADLOCK DETECTED: Hub {Address} made no teardown progress for {Timeout} "
            + "(last progress: {LastProgress}). RunLevel={RunLevel}, queue depth {Depth}, "
            + "drainsInFlight={DrainsInFlight}, drainsAwaitingScheduler={DrainsAwaiting}, "
            + "draining={Draining}, {Dequeued} turn(s) dequeued since "
            + "Dispose(). No turn is on the block, the pump is not holding queued work, and this hub "
            + "has no hosted hubs and no outstanding child-disposal join — so THIS VERDICT DOES NOT "
            + "NAME A CAUSE. What is still outstanding is in the diagnostics below (pending callbacks "
            + "are the usual one: a reply owed from outside this mesh). Disposal is NOT "
            + "forced.\n{Diagnostics}",
            Address, DisposalWatchdogTimeout, lastProgress, RunLevel, snapshot?.Buffer ?? -1,
            snapshot?.DrainsInFlight ?? -1, snapshot?.DrainsAwaitingScheduler ?? -1L,
            snapshot?.Draining ?? false,
            dequeued - disposalDequeuedBaseline, DescribeWedge());
    }

    /// <summary>
    /// The evidence that turns "DISPOSAL DEADLOCK DETECTED" into a diagnosis: the recursive
    /// disposal snapshot (<see cref="GetDisposalDiagnostics"/>) — every hosted hub's RunLevel,
    /// queue depths, the message currently on its action block, and its pending callbacks.
    ///
    /// <para>🚨 Without this the error names a PHASE and nothing else, and the four mechanisms
    /// that produce <c>RunLevel=DisposeHostedHubs</c> are indistinguishable from the log: a child
    /// whose pump is starved behind a FIFO backlog (<c>buffer</c> in the hundreds, <c>Executing</c>
    /// a few ms), a child frozen on one non-terminating turn (<c>buffer</c> small,
    /// <c>Executing</c> 8000+ ms and NAMED), an in-flight hosted-hub construction the join is
    /// correctly waiting out, or simply a child that answers on its OWN watchdog — which is armed
    /// one quiesce budget LATER than this one and therefore cannot answer before it. #1701 sat on
    /// "the compile-pool children are the prime suspects" for exactly this reason: the log records
    /// the parent's phase and nothing about the child that is holding it.</para>
    ///
    /// <para>Never throws: a diagnostic that can fault the teardown it is diagnosing is worse than
    /// no diagnostic. The tree walk is depth-capped (<see cref="MaxHostedHubRecursionDepth"/>) and
    /// runs once, at the moment a deadlock has already been detected.</para>
    /// </summary>
    private string DescribeWedge()
    {
        try
        {
            return GetDisposalDiagnostics();
        }
        catch (Exception e)
        {
            return $"(disposal diagnostics unavailable: {e.GetType().Name}: {e.Message})";
        }
    }

    private void DisposeImpl()
    {
        // 1. Fire the REACTIVE dispose actions. Each returns IObservable<Unit>; we
        //    compose them into one chain (Merge) and subscribe — fire-and-forget. Their
        //    genuinely-async leaves (e.g. a final storage flush, a remote unsubscribe)
        //    run on the mesh IO pool, which outlives this hub, so the work completes in
        //    the background. The hub never awaits — nothing on this path is a Task. The
        //    chain is NOT held in `disposables`, so the synchronous teardown below can't
        //    cancel an in-flight pool leaf.
        ImmutableList<Func<IMessageHub, IObservable<Unit>>> reactive;
        lock (reactiveDisposeLock)
        {
            reactive = reactiveDisposeActions;
            reactiveDisposeActions = ImmutableList<Func<IMessageHub, IObservable<Unit>>>.Empty;
        }
        if (!reactive.IsEmpty)
        {
            var legs = reactive.Select(action =>
            {
                try
                {
                    return action(this).Catch<Unit, Exception>(ex =>
                    {
                        TryLog(LogLevel.Warning, "[DISPOSE-ACTION] {Address}: dispose action faulted: {Type}: {Message}",
                            Address, ex.GetType().Name, ex.Message);
                        return Observable.Return(Unit.Default);
                    });
                }
                catch (Exception ex)
                {
                    TryLog(LogLevel.Warning, "[DISPOSE-ACTION] {Address}: dispose action threw synchronously: {Type}: {Message}",
                        Address, ex.GetType().Name, ex.Message);
                    return Observable.Return(Unit.Default);
                }
            });
            // Each leg is Catch-wrapped above, so a fault here is unexpected — log it
            // rather than swallowing (an empty onError hides plumbing bugs in Merge).
            Observable.Merge(legs).Subscribe(
                _ => { },
                ex => TryLog(LogLevel.Warning,
                    "[DISPOSE-ACTION] {Address}: dispose-action merge faulted: {Type}: {Message}",
                    Address, ex.GetType().Name, ex.Message));
        }

        // 2. Synchronous teardown of every registered subscription / Action cleanup —
        //    normal Rx subscription logic, no bag. Every registrant went in wrapped by
        //    GuardRegistrant, so one that throws is named and the walk continues.
        disposables.Dispose();
    }

    /// <summary>
    /// Wraps a registered cleanup so a fault in it is NAMED and the teardown continues.
    ///
    /// <para>🚨 Rx's <c>CompositeDisposable.Dispose</c> walks its list with no per-item guard, so
    /// the FIRST registrant that throws ends the walk and every cleanup registered after it is
    /// silently skipped — a subscription left live, a NACK never minted, and nothing in the log
    /// naming which registrant did it (the caller sees one warning attributed to
    /// <c>DisposeImpl</c> as a whole). Measured on main shard 4, 2026-09-02: one disposal action
    /// resolving out of an already-closed lifetime scope threw <c>ObjectDisposedException</c>
    /// here, and the <c>OwnerDisposing</c> NACK behind it never reached its waiting writer, which
    /// then burned the full 31 s verdict budget (<c>LateNackReenqueueTest</c>, run 33630685580).</para>
    ///
    /// <para>This swallows nothing: the fault is logged with the registrant that raised it, and
    /// the remaining cleanups still run. A registrant that throws is a bug IN THAT REGISTRANT —
    /// it is now visible as one instead of as a truncated teardown. It is the same per-leg
    /// isolation the reactive dispose actions already have in <see cref="DisposeImpl"/>.</para>
    /// </summary>
    private IDisposable GuardRegistrant(IDisposable registrant) =>
        System.Reactive.Disposables.Disposable.Create(() =>
        {
            try { registrant.Dispose(); }
            catch (Exception e)
            {
                // 🚨 The EXCEPTION, not its ToString(): a registrant that throws here is a bug in
                // that registrant, and the only thing that says WHICH line raised it is the stack
                // trace. Type+message alone names the symptom and hides the site — and this arm is
                // now the ONLY report of that fault, since the walk no longer ends on it.
                TryLog(LogLevel.Warning, e,
                    "[DISPOSE-REGISTRANT] {Address}: a registered cleanup ({Registrant}) faulted. "
                    + "The remaining cleanups still ran.",
                    Address, registrant.GetType().Name);
            }
        });

    /// <summary>
    /// Multi-line snapshot of the hub's disposal state. Reports own RunLevel + disposal
    /// status, the message service's per-buffer counts (so a backlog from a handler that keeps
    /// re-posting shows up as a non-zero queue), and a one-line entry per hosted hub
    /// recursively. Test base classes call this when a dispose timeout fires; the returned
    /// string is meant to land in xUnit test output so the failure says *why* dispose hung
    /// rather than just "operation was canceled".
    /// </summary>
    public string GetDisposalDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        AppendDiagnostics(sb, depth: 0);
        return sb.ToString();
    }

    /// <summary>
    /// Single-line snapshot of THIS hub's in-flight state — queue depths, the message
    /// currently executing on the action block, and the outstanding response callbacks.
    /// Deliberately NOT recursive (unlike <see cref="GetDisposalDiagnostics"/>, which walks
    /// the whole hosted-hub tree): this one is cheap enough to attach to a live request
    /// timeout on a portal mesh hub that hosts hundreds of per-node hubs.
    /// </summary>
    /// <returns>A one-line diagnostic string; never null.</returns>
    public string GetPendingRequestDiagnostics()
    {
        var snapshot = (messageService is MessageService ms)
            ? ms.GetQueueSnapshot()
            : (Buffer: -1, Deferred: -1, DrainsInFlight: -1, OpenGates: -1, Draining: false,
               CurrentMessage: (string?)null, CurrentMessageElapsedMs: 0L,
               DrainsAwaitingScheduler: -1L);
        var pending = SnapshotPendingCallbacks();
        var sb = new System.Text.StringBuilder();
        sb.Append("Hub ").Append(Address)
          .Append(" RunLevel=").Append(RunLevel)
          .Append(" Queue(buffer=").Append(snapshot.Buffer)
          .Append(",deferred=").Append(snapshot.Deferred)
          .Append(",drainsInFlight=").Append(snapshot.DrainsInFlight)
          .Append(')');
        if (snapshot.CurrentMessage != null)
            sb.Append(" Executing(").Append(snapshot.CurrentMessage)
              .Append(", ").Append(snapshot.CurrentMessageElapsedMs).Append("ms)");
        sb.Append(" PendingCallbacks=").Append(pending.Length)
          .Append('[').Append(FormatPendingCallbacks(pending)).Append(']')
          // See DisposalRegistrantCount: append-only composite, so a number that climbs with the
          // traffic this hub has served is one retained object graph per unit of that traffic.
          .Append(" Registrants=").Append(DisposalRegistrantCount);
        return sb.ToString();
    }

    /// <summary>
    /// Hard cap on hosted-hub tree recursion. Real hierarchies are at most a few
    /// levels deep; anything beyond this is almost certainly a cycle (e.g. hub A
    /// hosts hub B whose configuration re-creates A). Without this guard the
    /// recursion would stack-overflow the test host on Linux, where the default
    /// stack is smaller than Windows and the unwind silently kills the process
    /// (no error, no trx update — just SIGTERM at the wall-clock cap).
    /// </summary>
    private const int MaxHostedHubRecursionDepth = 32;

    /// <summary>
    /// <c>true</c> if this hub or any hosted hub (recursively) hit the Quiescing-phase timeout — i.e.
    /// had response callbacks still pending when the dispose drain budget elapsed. Tests treat this as
    /// a dispose failure (a leaked subscription that never received its reply).
    /// </summary>
    /// <returns><c>true</c> if any hub in the tree timed out during Quiescing.</returns>
    public bool AnyHubQuiescingTimedOut() => AnyHubQuiescingTimedOut(depth: 0);

    private bool AnyHubQuiescingTimedOut(int depth)
    {
        if (QuiescingTimedOut) return true;
        if (depth >= MaxHostedHubRecursionDepth) return false;
        foreach (var child in hostedHubs.Hubs)
            if (child is MessageHub childMh && childMh.AnyHubQuiescingTimedOut(depth + 1)) return true;
        return false;
    }

    /// <summary>
    /// 🚨 <b>Every hub alive under this one, this one included — the population #3432 needed and
    /// nobody could see without a heap dump (#3488).</b>
    ///
    /// <para>Yielded lazily and bounded by <see cref="MaxHostedHubRecursionDepth"/>, exactly like
    /// the disposal walks beside it: a hosted-hub cycle must cost a truncated answer, never a hung
    /// scrape. The enumeration is over <c>HostedHubsCollection.Hubs</c>, which is a
    /// <c>ConcurrentDictionary</c>'s values — safe to walk while hubs are added and removed, and
    /// the snapshot is deliberately not locked: a metric of a moving population is a sample, and
    /// pausing the mesh to make it exact would cost more than the number is worth.</para>
    ///
    /// <para>Internal: this is instrumentation's read, not a public traversal API. A caller that
    /// wants to ACT on the tree should use the disposal seams, which are ordered.</para>
    /// </summary>
    /// <param name="depth">Recursion depth; callers pass nothing.</param>
    /// <returns>This hub, then every live descendant.</returns>
    internal IEnumerable<IMessageHub> LiveHubTree(int depth = 0)
    {
        yield return this;
        if (depth >= MaxHostedHubRecursionDepth)
            yield break;
        foreach (var child in hostedHubs.Hubs)
        {
            if (child is not MessageHub childHub)
            {
                yield return child;
                continue;
            }
            foreach (var descendant in childHub.LiveHubTree(depth + 1))
                yield return descendant;
        }
    }

    /// <summary>
    /// Builds a concise, indented summary of the hubs (and their pending callbacks) that hit the
    /// Quiescing timeout, for a dispose-failure message. Empty when none timed out.
    /// </summary>
    /// <returns>The multi-line summary, or an empty string when no hub timed out.</returns>
    public string GetQuiescingTimeoutSummary()
    {
        var sb = new System.Text.StringBuilder();
        AppendQuiescingTimeoutSummary(sb, depth: 0);
        return sb.ToString();
    }

    private void AppendQuiescingTimeoutSummary(System.Text.StringBuilder sb, int depth)
    {
        if (depth >= MaxHostedHubRecursionDepth)
        {
            sb.Append(new string(' ', depth * 2))
              .AppendLine("(recursion depth limit reached — possible hosted-hub cycle)");
            return;
        }
        if (QuiescingTimedOut)
        {
            sb.Append(new string(' ', depth * 2))
              .Append("Hub ").Append(Address).Append(": ")
              .AppendLine(QuiescingTimeoutDetail ?? "(no detail captured)");
        }
        foreach (var child in hostedHubs.Hubs)
            if (child is MessageHub childMh)
                childMh.AppendQuiescingTimeoutSummary(sb, depth + 1);
    }

    private void AppendDiagnostics(System.Text.StringBuilder sb, int depth)
    {
        if (depth >= MaxHostedHubRecursionDepth)
        {
            sb.Append(new string(' ', depth * 2))
              .AppendLine("(recursion depth limit reached — possible hosted-hub cycle)");
            return;
        }
        var indent = new string(' ', depth * 2);
        var snapshot = (messageService is MessageService ms)
            ? ms.GetQueueSnapshot()
            : (Buffer: -1, Deferred: -1, DrainsInFlight: -1, OpenGates: -1, Draining: false,
               CurrentMessage: (string?)null, CurrentMessageElapsedMs: 0L,
               DrainsAwaitingScheduler: -1L);
        sb.Append(indent)
          .Append("Hub ").Append(Address)
          .Append(" RunLevel=").Append(RunLevel)
          .Append(" Disposal=")
          .Append(!disposalStarted ? "<not started>"
              : DisposalSignalled ? "Completed" : "Pending")
          .Append(" Queue(buffer=").Append(snapshot.Buffer)
          .Append(",deferred=").Append(snapshot.Deferred)
          .Append(",drainsInFlight=").Append(snapshot.DrainsInFlight)
          .Append(",openGates=").Append(snapshot.OpenGates)
          .Append(",draining=").Append(snapshot.Draining)
          .Append(')');
        if (snapshot.CurrentMessage != null)
        {
            sb.Append(" Executing(")
              .Append(snapshot.CurrentMessage)
              .Append(", ")
              .Append(snapshot.CurrentMessageElapsedMs)
              .Append("ms)");
        }
        // Pending callbacks: same data as the [QUIESCE-START] / [QUIESCE-TIMEOUT] log
        // lines, but folded into the test-base [DISPOSE] snapshot so a 30 s test-base
        // dispose timeout names *what* was outstanding even when structured logs
        // aren't visible to the test author.
        var pending = SnapshotPendingCallbacks();
        if (pending.Length > 0)
            sb.Append(" PendingCallbacks=").Append(pending.Length)
              .Append('[').Append(FormatPendingCallbacks(pending)).Append(']')
              // …and the handler side (#981). This snapshot is what the test base writes on a
              // DISPOSE_TIMEOUT — the case where the hub never even reached its quiescing
              // timeout, so the enriched QuiescingTimeoutDetail does not exist yet.
              .Append(FormatPendingCallbackFates(pending));
        // 🚨 The RETENTION field (#3432). See DisposalRegistrantCount: the composite is
        // append-only, so this number is the count of object graphs this hub is holding through
        // registered cleanups — and a hub whose registrant count climbs with the traffic it has
        // served is retaining one per unit of that traffic. Printed unconditionally so "I measured
        // it and it was small" and "I did not measure it" are two different lines.
        sb.Append(" Registrants=").Append(DisposalRegistrantCount);
        sb.AppendLine();

        var hosted = hostedHubs.Hubs.ToArray();
        if (hosted.Length == 0)
            return;
        sb.Append(indent).Append("HostedHubs (").Append(hosted.Length).Append("):").AppendLine();
        foreach (var child in hosted)
        {
            if (child is MessageHub childMh)
                childMh.AppendDiagnostics(sb, depth + 1);
            else
                sb.Append(indent).Append("  Hub ").Append(child.Address).AppendLine();
        }
    }
    // Fully SYNCHRONOUS. The phase handlers kick off their waits as Rx subscriptions
    // (Observable.Interval / Timer / hostedHubs.DisposalCompleted) and return immediately —
    // nothing on this path awaits, and the action block is never blocked. Completion is
    // signalled through the `disposalCompleted` ReplaySubject.
    private IMessageDelivery HandleShutdownCore(
        IMessageDelivery<ShutdownRequest> request
    )
    {
        var phaseStopwatch = Stopwatch.StartNew();
        shutdownTurnsHandled++;
        logger.LogDebug("STARTING HandleShutdown for hub {Address}, RunLevel={RunLevel}, RequestVersion={RequestVersion}, total disposal time so far: {totalElapsed}ms",
            Address, request.Message.RunLevel, request.Message.Version, disposalStopwatch.ElapsedMilliseconds);

        // Registered cleanups (subscriptions, Action lambdas, pool-bridged disposables)
        // are disposed synchronously later, in the ShutDown phase (DisposeImpl →
        // disposables.Dispose). There is no async dispose-action drain phase.

        // NO version-match gate here. We used to require request.Version == Version - 1
        // (i.e. "this ShutdownRequest is the immediately-next message since it was
        // posted, nothing handled in between") and, on mismatch, REPOST the request with
        // a corrected version. On a busy hub that was a livelock: ++Version runs for
        // EVERY message (HandleMessageAsync), so any concurrent traffic between a repost
        // and its re-handle bumps Version past the one-step window — the gate never
        // converges and instead self-sustains a repost STORM (2,820 ShutdownRequest
        // reposts on a single `consumer/1` hub under the 2-core security tests; 140k
        // ShutdownRequest turns suite-wide, saturating TaskScheduler.Default and timing
        // the project out under the 2-core CI runner). The gate also added NOTHING:
        // duplicates are already handled by the per-phase RunLevel idempotency guards
        // below (Ignored, not reposted), and the three phases are causally chained
        // (Quiescing → DisposeHostedHubs → ShutDown, each posted from the previous
        // phase's completion) and FIFO-ordered, so order is guaranteed without it.
        switch (request.Message.RunLevel)
        {
            case MessageHubRunLevel.Quiescing:
                lock (locker)
                {
                    if (RunLevel >= MessageHubRunLevel.Quiescing)
                    {
                        TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: Quiescing already processed (RunLevel={runLevel}), ignoring",
                            Address, RunLevel);
                        return request.Ignored();
                    }
                    RunLevel = MessageHubRunLevel.Quiescing;
                }

                // Stop the always-on stale-callback scanner so it doesn't fire
                // during the quiesce wait (its warnings would be redundant with
                // [QUIESCE-START]/[QUIESCE-TIMEOUT]). Idempotent dispose.
                staleCallbackScannerSub?.Dispose();
                staleCallbackScannerSub = null;

                var initialPendingSnapshot = SnapshotPendingCallbacks();
                TryLog(LogLevel.Information,
                    "[QUIESCE-START] {Address}: requested by {RequestedBy}; why: {Reason}; "
                    + "{Count} pending callbacks at dispose entry: {Pending}",
                    Address, DisposalRequestedBy, DisposalReason,
                    initialPendingSnapshot.Length, FormatPendingCallbacks(initialPendingSnapshot));

                // CRITICAL: do the wait OFF the action block. The action block processes
                // messages serially (MaxDegreeOfParallelism = 1) — a blocking wait on the
                // action-block thread would stop dequeuing the very response messages we're
                // waiting to drain (a self-deadlock → guaranteed QuiesceTimeout).
                //
                // Reactive poll: `Observable.Interval` ticks on the DefaultScheduler (off the
                // action block); each tick checks whether `responseSubjects` has drained. The
                // leading StartWith(-1) probes ONCE inline (so an already-drained hub advances
                // without a scheduler hop). `Amb` races the drain signal against a single
                // `Observable.Timer(QuiesceTimeout)` deadline and takes whichever fires first —
                // drained → true, budget exceeded → false. (Note: a between-emissions
                // `.Timeout` would NOT work here — the interval emits every QuiescePollInterval,
                // so the inter-emission gap never reaches QuiesceTimeout; the deadline must be a
                // separate total-duration timer.) The result funnels into OnQuiesceComplete,
                // which posts DisposeHostedHubs. No Task.Delay, no await — the handler returns
                // immediately and the action block stays free.
                StartQuiesceWait(Stopwatch.StartNew(), initialPendingSnapshot);
                break;
            case MessageHubRunLevel.DisposeHostedHubs:
                var disposeHostedHubsStopwatch = Stopwatch.StartNew();
                lock (locker)
                {
                    if (RunLevel == MessageHubRunLevel.DisposeHostedHubs)
                    {
                        TryLog(LogLevel.Warning,
                            "DisposeHostedHubs already processed for hub {Address}, ignoring (phase time: {elapsed}ms)",
                            Address, phaseStopwatch.ElapsedMilliseconds);
                        return request.Ignored();
                    }

                    RunLevel = MessageHubRunLevel.DisposeHostedHubs;
                }

                TryLog(LogLevel.Debug,
                    "[HOSTED-DISPOSE-START] {Address}: disposing {Count} hosted hub(s) at {Elapsed}ms",
                    Address, hostedHubs.Hubs.Count(), disposalStopwatch.ElapsedMilliseconds);
                // Dispose the hosted hubs (each disposes synchronously) and OBSERVE their
                // collective completion via the collection's `DisposalCompleted` observable —
                // no `await hostedHubs.Disposal`, no Task.Run. Each child hub completes its own
                // reactive disposal and the collection completes once all have. On completion
                // OR error, advance to ShutDown. The collection carries NO deadline of its own
                // (#1317) — this hub's stall detector (OnDisposalStall) watches the whole phase
                // and names a child that stops moving; nothing here gives up on a child or tears
                // it down mid-disposal.
                hostedHubs.Dispose();
                var hostedSw = disposeHostedHubsStopwatch;
                hostedHubsDisposalSubscription = hostedHubs.DisposalCompleted
                    .Take(1)
                    .Subscribe(
                        _ => { },
                        ex =>
                        {
                            // A hosted-hub disposal fault must SURFACE — this arm previously
                            // recorded it only into an off-by-default trace file, i.e. nowhere.
                            TryLog(LogLevel.Warning, ex,
                                "[HOSTED-DISPOSE-ERROR] {Address}: hosted hub disposal faulted after {Elapsed}ms",
                                Address, hostedSw.ElapsedMilliseconds);
                            PostShutDownPhase(hostedSw);
                        },
                        () =>
                        {
                            TryLog(LogLevel.Debug,
                                "[HOSTED-DISPOSE-OK] {Address}: hosted hubs disposed in {Elapsed}ms",
                                Address, hostedSw.ElapsedMilliseconds);
                            PostShutDownPhase(hostedSw);
                        });
                break;
            case MessageHubRunLevel.ShutDown:
                var shutdownStopwatch = Stopwatch.StartNew();
                try
                {
                    lock (locker)
                    {
                        if (RunLevel == MessageHubRunLevel.ShutDown)
                        {
                            logger.LogDebug("[DISPOSE-TRACE] {address}: ShutDown already processed, ignoring", Address);
                            return request.Ignored();
                        }

                        logger.LogDebug("[DISPOSE-TRACE] {address}: STARTING ShutDown phase", Address);
                        RunLevel = MessageHubRunLevel.ShutDown;
                    }

                    CancelCallbacks();
                    DisposeImpl();

                    logger.LogDebug("[DISPOSE-TRACE] {address}: messageService.Dispose() (sync)...", Address);
                    messageService.Dispose();
                    logger.LogDebug("[DISPOSE-TRACE] {address}: messageService.Dispose() done in {elapsed}ms",
                        Address, shutdownStopwatch.ElapsedMilliseconds);

                    // 🚨 The hub does NOT close its own container here — the thing that OPENED the
                    // scope closes it, once this hub is terminally down. See
                    // `HostedHubsCollection.CloseScopeWhenDisposed`, which subscribes to the
                    // `DisposalCompleted` signalled a few lines below. Two reasons it cannot live
                    // here: a hub is a singleton IN the scope it would be destroying, so it would
                    // be pulling its own logger and the rest of the `finally` out from under
                    // itself; and a hub disposed on its own (a recycle, not a teardown) is exactly
                    // the case that leaks, and it never reaches the branch a parent's teardown
                    // takes. What it costs when NOBODY closes it is in that method's remarks.

                    // Dead BEFORE signalling — callers awaiting DisposalCompleted must observe the
                    // terminal state, never a mid-teardown ShutDown snapshot. Dead used to be set
                    // only in the FINALLY here, so a
                    // StopAsync waiter woken by the signal could finish its (fast) IoPool +
                    // AsyncDisposeQueue drains and read RunLevel == ShutDown — the flaky
                    // MeshHostBuilderTeardownOrderingTest failure on loaded CI shards. The finally
                    // keeps its (now redundant on success) assignment for the faulted path.
                    lock (locker)
                    {
                        RunLevel = MessageHubRunLevel.Dead;
                    }
                    // Signal completion through the reactive source (idempotent CAS — no
                    // InvalidOperationException to guard, unlike TaskCompletionSource).
                    SignalDisposalCompleted();
                    logger.LogDebug("[DISPOSE-TRACE] {address}: Disposal COMPLETED in {elapsed}ms total",
                        Address, disposalStopwatch.ElapsedMilliseconds);
                }
                catch (Exception e)
                {
                    logger.LogError("Error during shutdown of hub {address} after {elapsed}ms (total disposal time: {totalElapsed}ms): {exception}",
                        Address, shutdownStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds, e);
                    // Dead BEFORE signalling on the FAULT path too — TeardownAsync catches
                    // DisposalCompleted errors and continues, so a faulted-signal waiter reads
                    // RunLevel exactly like a completed-signal waiter does.
                    lock (locker)
                    {
                        RunLevel = MessageHubRunLevel.Dead;
                    }
                    SignalDisposalFaulted(e);
                }
                finally
                {
                    // Backstop only — both signal paths above already terminalized under the lock.
                    lock (locker)
                    {
                        RunLevel = MessageHubRunLevel.Dead;
                    }
                    disposalStopwatch.Stop();
                    // Tidy the disposal-phase subscriptions (each has already self-completed:
                    // the watchdog cancelled via TakeUntil when SignalDisposalCompleted fired,
                    // the quiescing/dispose-action/hosted-hub subscriptions completed before
                    // posting onward).
                    watchdogSubscription?.Dispose();
                    quiescingSubscription?.Dispose();
                    hostedHubsDisposalSubscription?.Dispose();
                    //await ((IAsyncDisposable)ServiceProvider).DisposeAsync();
                    // Use parentAddress captured at construction — Configuration.ParentHub
                    // re-resolves from ParentServiceProvider, which is often disposed by the
                    // time we get here, throwing ObjectDisposedException that pollutes test
                    // logs. Never call DI from a disposal path.
                    logger.LogDebug("Finished shutdown of hub {address} with parent {parent} - final phase took {elapsed}ms, total disposal time: {totalElapsed}ms",
                        Address, parentAddress, phaseStopwatch.ElapsedMilliseconds, disposalStopwatch.ElapsedMilliseconds);
                }

                break;
        }

        return request.Processed();
    }

    /// <summary>
    /// True when this hub's <see cref="ServiceProvider"/> is a lifetime scope the configuration
    /// opened for it (see <see cref="MessageHubConfiguration.OwnsServiceProvider"/>), so somebody
    /// has to close it. Read by <see cref="HostedHubsCollection.Add"/>, which is the only place
    /// that both knows the scope exists and can act strictly after this hub is terminally down.
    /// </summary>
    internal bool OwnsServiceProvider => Configuration.OwnsServiceProvider;

    /// <summary>
    /// One Quiescing wait: the reactive drain poll raced against one <see cref="QuiesceTimeout"/>
    /// deadline, funnelled into <see cref="OnQuiesceComplete"/>. Called once from the Quiescing
    /// phase and again by <see cref="OnQuiesceComplete"/> when the budget expired on replies that a
    /// shutting-down sibling still owes (see <see cref="AReplyIsOwedByAShuttingDownLocalHub"/>).
    /// </summary>
    private void StartQuiesceWait(
        Stopwatch quiesceSw,
        (string MessageId, string RequestType, Address? Target, long AgeMs, string? DiagnosticKey)[]
            initialPendingSnapshot)
    {
        var drained = Observable
            .Interval(QuiescePollInterval)
            .StartWith(-1L)
            .Select(_ => { lock (responseSubjects) return responseSubjects.Count == 0; })
            .Where(empty => empty)
            .Take(1)
            .Select(_ => true);
        var quiesceDeadline = Observable.Timer(QuiesceTimeout).Select(_ => false);
        quiescingSubscription = drained
            .Amb(quiesceDeadline)
            .Take(1)
            .Subscribe(
                drainedOk => OnQuiesceComplete(drainedOk, quiesceSw, initialPendingSnapshot),
                _ => OnQuiesceComplete(drainedOk: false, quiesceSw, initialPendingSnapshot));
    }

    /// <summary>
    /// How many times the Quiescing budget may be re-armed on replies a shutting-down sibling still
    /// owes before the wait is cut and the callbacks cancelled. The wait normally ends by
    /// construction — the sibling answers or leaves the registry — so this is the cycle breaker
    /// for two hubs each holding a deferred request of the other's, never a duration to tune.
    /// </summary>
    private const int MaxQuiesceRearms = 20;
    private int quiesceRearms;

    /// <summary>
    /// Hands a <see cref="DeliveryFailure"/> that no transport can carry DIRECTLY to the in-process
    /// hub its requester lives under, when that hub still admits replies — the carrier of last
    /// resort for an answer minted during a whole-tree teardown (#4072).
    ///
    /// <para><b>Why a NACK needs its own carrier.</b> Every route out of a hub tearing down goes
    /// through its parent: <c>NackThroughParent</c> posts through it and
    /// <c>PostImplGeneric</c> forwards a correlated reply through it. Both decline the moment the
    /// parent is itself past <see cref="MessageHubRunLevel.DisposeHostedHubs"/>, and at a
    /// whole-tree teardown that is the state EVERY sibling is answered in — the parent disposes
    /// its children only after reaching that phase. The comment those declines carried read
    /// "every sender is going away too", which is not what happens: a sibling that is
    /// <see cref="MessageHubRunLevel.Quiescing"/> is WAITING for exactly this answer, re-arms its
    /// budget for it ([QUIESCE-WAIT], <see cref="AReplyIsOwedByAShuttingDownLocalHub"/>), and
    /// only gives up on <c>[QUIESCE-CUT]</c> — so the whole teardown paces itself on the sum of
    /// those budgets for an answer that existed the whole time. Measured on
    /// <c>LeavingHubAdoptionSweepTest</c> (14 s, PASSING): the responder's trail ended
    /// <c>NACK_DECLINED reason=parent-DisposeHostedHubs → FAILURE_REPORTED → RESPONSE_POSTED →
    /// REPLY_REFUSED_SHUTTING_DOWN runLevel=Dead parent=mesh/…@DisposeHostedHubs</c>, while the
    /// requester sat Quiescing with that callback pending for 4 s.</para>
    ///
    /// <para><b>Why this is not a second transport.</b> It is offered ONLY where the delivery is
    /// being dropped — after the parent route and <see cref="IUndeliverableReplySink"/> have both
    /// declined — so there is no post left for it to race, the precondition
    /// <c>Doc/Architecture/RefusedRepliesDuringTeardown</c> requires. And it carries a NACK ONLY:
    /// a <see cref="DeliveryFailure"/> acknowledges no state, so the reorder hazard that keeps
    /// typed replies off any bypass (an ack overtaking the change it acknowledges) cannot arise.
    /// The delivery lands on the requester's own intake — its gate decides, its pump serialises —
    /// exactly as a routed delivery would; nothing is dispatched on the responder's turn.</para>
    ///
    /// <para>Resolution mirrors <see cref="AReplyIsOwedByAShuttingDownLocalHub"/>: climb the
    /// target's host chain, look the top-level hub up under the root (never create), then shorter
    /// prefixes for a hub hosting a sub-path. A remote requester, a requester the root does not
    /// host, or one already past <see cref="MessageHubRunLevel.DisposeHostedHubs"/> (its intake
    /// refuses and its callbacks are cancelled) leaves the drop as it was.</para>
    /// </summary>
    /// <param name="failure">The NACK, targeted at the requester and carrying <c>PostOptions.RequestId</c>.</param>
    /// <returns><c>true</c> when the requester's hub accepted the delivery into its intake.</returns>
    internal bool TryDeliverNackInProcess(IMessageDelivery failure)
    {
        if (failure.Message is not DeliveryFailure
            || !failure.Properties.ContainsKey(PostOptions.RequestId)
            || failure.Target is not { } target)
            return false;
        try
        {
            IMessageHub root = this;
            while ((root as MessageHub)?.messageService is MessageService ms && ms.ParentHub is { } parent)
                root = parent;
            // The requester's host chain, OUTERMOST first: routing up through a non-mesh parent
            // stamps the sender with that parent (HierarchicalRouting), so a requester nested two
            // levels down reads `R{Host=P}` and resolves as root → P → R. Only the LAST hub has
            // to admit the delivery; the ones in between are looked up, never posted to, so their
            // own run level does not matter (a collection past DisposeHostedHubs still resolves
            // existing hubs — it only refuses to CREATE).
            var levels = ImmutableList<Address>.Empty;
            for (var level = target; level is not null; level = level.Host)
                levels = levels.Insert(0, level with { Host = null });
            var requester = ResolveTopLevelHub(root, levels[0]);
            for (var i = 1; requester is not null && i < levels.Count; i++)
                requester = requester.GetHostedHub(levels[i], HostedHubCreation.Never);
            if (requester is null
                || ReferenceEquals(requester, this)
                || requester.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                return false;
            // The intake's verdict IS the answer: the requester can cross DisposeHostedHubs
            // between the check above and this call, and its gate then hands back Failed
            // (or Ignored from the storm breaker) instead of enqueueing. Claiming "delivered" on
            // that would suppress every remaining carrier for a callback that was never resolved.
            var accepted = requester.DeliverMessage(failure);
            return accepted.WasAcceptedForDelivery;
        }
        catch (ObjectDisposedException)
        {
            // A hosted collection on the way is gone: nothing here can take it, the drop stands.
        }
        return false;
    }

    /// <summary>
    /// The hub under <paramref name="root"/> that hosts <paramref name="top"/> — the address
    /// itself, then shorter path prefixes for a hub hosting a sub-path — never created. Mirrors
    /// the resolution <see cref="AReplyIsOwedByAShuttingDownLocalHub"/> and
    /// <c>HierarchicalRouting</c> perform at the root.
    /// </summary>
    private static IMessageHub? ResolveTopLevelHub(IMessageHub root, Address top)
    {
        if (root.Address.Equals(top))
            return root;
        var segments = top.Segments;
        for (var k = segments.Length; k >= 1; k--)
        {
            var candidate = k == segments.Length ? top : new Address(segments[..k]);
            if (root.GetHostedHub(candidate, HostedHubCreation.Never) is { } hosted)
                return hosted;
        }
        return null;
    }

    /// <summary>
    /// True when a pending reply for <paramref name="target"/> is guaranteed to arrive from a hub in
    /// THIS mesh that is itself shutting down. Such a hub answers every delivery it accepted before
    /// it signals Dead and leaves its owner's registry: served ahead of its own ShutdownRequest, or
    /// NACKed <c>ShuttingDown</c> by its <c>messageService.Dispose()</c> (DISPOSE-DISCARD). A
    /// Quiescing hub therefore WAITS for that reply instead of cancelling its callback on a
    /// duration — the wait ends by construction when the sibling has answered or gone.
    ///
    /// <para>Measured on the #3261 detector: every callback the 0.5 s test budget cancelled was a
    /// request to a sibling disposing concurrently — <c>CreateOrUpdateNodeRequest@portal/nodeops-*</c>,
    /// <c>SubscribeRequest@cache/*</c> — whose answer was on its way; production shows the same
    /// <c>[QUIESCE-TIMEOUT] … CreateNodeRequest@portal/nodeops-*</c> at 2 s. Cancelling those was
    /// discarding accepted work and reporting a leak that was not one.</para>
    ///
    /// <para>Resolution mirrors <c>HierarchicalRouting</c> at the root: the address's host chain is
    /// climbed and the top-level hub is looked up (never created), then shorter prefixes for a hub
    /// hosting a sub-path. Only hubs directly under the mesh root are recognised; anything else
    /// — a remote address, a nested hub, a live hub that is not disposing — keeps today's budget.</para>
    /// </summary>
    private bool AReplyIsOwedByAShuttingDownLocalHub(Address? target)
    {
        if (target is null)
            return false;
        try
        {
            IMessageHub root = this;
            while ((root as MessageHub)?.messageService is MessageService ms && ms.ParentHub is { } parent)
                root = parent;
            if (ReferenceEquals(root, this))
                return false;
            var top = target;
            while (top.Host is not null)
                top = top.Host;
            var segments = top.Segments;
            for (var k = segments.Length; k >= 1; k--)
            {
                var candidate = k == segments.Length ? top : new Address(segments[..k]);
                if (root.GetHostedHub(candidate, HostedHubCreation.Never) is not MessageHub sibling
                    || ReferenceEquals(sibling, this))
                    continue;
                return sibling.IsShuttingDown && !sibling.DisposalSignalled;
            }
        }
        catch (ObjectDisposedException)
        {
            // The registry or a scope is already gone — nothing there will answer.
        }
        return false;
    }

    /// <summary>
    /// Terminal step of the reactive Quiescing poll (see the Quiescing branch of
    /// <see cref="HandleShutdownCore"/>). Runs on the poll's scheduler thread when the
    /// response subjects drain (<paramref name="drainedOk"/> = true) or the
    /// <see cref="QuiesceTimeout"/> elapses (false). Logs the outcome, marks
    /// <see cref="QuiescingTimedOut"/> on timeout, then advances the state machine by posting
    /// the DisposeHostedHubs phase. Never throws — a wedged state machine is worse than a hang.
    /// </summary>
    private void OnQuiesceComplete(
        bool drainedOk,
        Stopwatch quiesceSw,
        (string MessageId, string RequestType, Address? Target, long AgeMs, string? DiagnosticKey)[]
            initialPendingSnapshot)
    {
        var advance = true;
        try
        {
            if (drainedOk)
            {
                TryLog(LogLevel.Information,
                    "[QUIESCE-OK] {Address}: drained {Count} callback(s) in {Elapsed}ms",
                    Address, initialPendingSnapshot.Length, quiesceSw.ElapsedMilliseconds);
            }
            else
            {
                var stuck = SnapshotPendingCallbacks();
                var owed = stuck.Where(c => AReplyIsOwedByAShuttingDownLocalHub(c.Target)).ToArray();
                if (owed.Length > 0 && quiesceRearms < MaxQuiesceRearms)
                {
                    // Accepted work, still being answered: the sibling that owes each reply is in
                    // this mesh and shutting down, so it WILL answer before it goes. Re-arm the
                    // budget instead of cancelling — the wait ends when the reply lands or the
                    // sibling has left the registry, whichever comes first.
                    quiesceRearms++;
                    TryLog(LogLevel.Information,
                        "[QUIESCE-WAIT] {Address}: {Count} callback(s) still pending after {Elapsed}ms are owed by "
                        + "hub(s) in this mesh that are themselves shutting down and answer before they go — "
                        + "waiting (re-arm {Rearm}/{Max}), not cancelling: {Pending}",
                        Address, owed.Length, quiesceSw.ElapsedMilliseconds, quiesceRearms, MaxQuiesceRearms,
                        FormatPendingCallbacks(owed));
                    advance = false;
                    quiescingSubscription?.Dispose();
                    StartQuiesceWait(quiesceSw, initialPendingSnapshot);
                    return;
                }
                if (owed.Length > 0)
                    logger.LogError(DisposalQuiesceWaitCutOff,
                        "[QUIESCE-CUT] Hub {Address}: {Count} reply(ies) owed by shutting-down hub(s) in this mesh "
                        + "never arrived across {Rearms} re-armed budgets ({Elapsed}ms) — most likely each side is "
                        + "holding a deferred request of the other's. Cancelling them now; the sender is answered "
                        + "with a disposal failure. Pending: {Pending}",
                        Address, owed.Length, quiesceRearms, quiesceSw.ElapsedMilliseconds, FormatPendingCallbacks(owed));
                var detail = FormatPendingCallbacks(stuck);
                // 🚨 The HANDLER side (issue #981). The line above is caller-only — it says a
                // request was posted and never answered. This one says what happened to the
                // delivery at the receiving end: whether it was ever received, routed, deferred,
                // dropped, handled, or answered. Rendered HERE, while the trails are still live,
                // because QuiescingTimeoutDetail is a captured string read long after
                // CancelCallbacks has torn the ledger entries down.
                var fates = FormatPendingCallbackFates(stuck);
                TryLog(LogLevel.Warning,
                    "[QUIESCE-TIMEOUT] {Address}: {Count} callback(s) still pending after {Timeout}s — forcibly cancelling. Pending: {Pending}{Fates}",
                    Address, stuck.Length, QuiesceTimeout.TotalSeconds, detail, fates);
                // Sticky flag — tests recursively inspect this and treat any hub with
                // QuiescingTimedOut=true as a dispose failure. Forces visibility on leaked
                // Observe subscriptions instead of silently extending dispose budgets.
                QuiescingTimedOut = true;
                QuiescingTimeoutDetail = stuck.Length == 0
                    // 🚨 NOT the leaked-callback failure, and it must not read like one. The poll
                    // said "not drained" and by the time the verdict was rendered the dictionary
                    // was EMPTY — every callback resolved inside the hand-off between the last
                    // poll tick and this line. There is nothing outstanding and so nothing to name;
                    // a report that says "0 pending callback(s): <none>" looks like the diagnostic
                    // failed to collect evidence, which sends an investigation hunting for a leak
                    // that is not there. 26 of the 30 quiesce-leak records in a local trace were
                    // this shape (TodoDataChangeWorkflowTest, TodoGraphIntegrationTest,
                    // TodoViewsTest) — all indistinguishable, at a glance, from issue #981.
                    ? $"drained DURING the {QuiesceTimeout.TotalSeconds:F2}s timeout hand-off: the poll "
                      + "reported not-drained, but no callback was outstanding by the time the verdict "
                      + "was rendered. Nothing leaked here — this is the detector racing its own "
                      + "budget, NOT a pending callback. Do not confuse it with a report that names "
                      + "a request and an age."
                    : $"{stuck.Length} pending callback(s) after {QuiesceTimeout.TotalSeconds:F2}s: {detail}{fates}";
                try { CancelCallbacks(); }
                catch (Exception cancelEx)
                {
                    TryLog(LogLevel.Warning, "[QUIESCE-TIMEOUT] {Address}: CancelCallbacks threw {Type}: {Message}",
                        Address, cancelEx.GetType().Name, cancelEx.Message);
                }
            }
        }
        catch (Exception quiesceEx)
        {
            // Never let this branch throw — that would wedge the dispose state machine at
            // Quiescing forever (worse than the original hang). Log best-effort and advance.
            TryLog(LogLevel.Error,
                "[QUIESCE-ERROR] {Address}: unexpected exception {Type}: {Message}; proceeding to DisposeHostedHubs anyway.",
                Address, quiesceEx.GetType().Name, quiesceEx.Message);
        }
        finally
        {
            // Advance to DisposeHostedHubs — unless the budget was re-armed on replies a
            // shutting-down sibling still owes. Registered subscriptions are disposed
            // synchronously later, in the ShutDown phase (DisposeImpl →
            // disposables.Dispose) — there is no async dispose-action drain to await.
            //
            // 🚨 WRAPPED, for the reason the catch above states about itself: a throw here "would
            // wedge the dispose state machine at Quiescing forever (worse than the original hang)"
            // — and this Post sat OUTSIDE that catch, in the finally, so the one statement whose
            // failure that comment warns about was the one statement not covered. Its sibling for
            // the very next transition, PostShutDownPhase, has always force-faulted disposal on a
            // failed Post so subscribers to DisposalCompleted never hang; this transition just did
            // not. #3593's 2026-09-19 population is a hub parked at Quiescing with an EMPTY queue
            // and an idle pump for 253 s — i.e. the request was never queued and nothing was
            // running, which is exactly the shape a lost Post here leaves behind.
            //
            // This is not a swallow: SignalDisposalFaulted TERMINATES disposal with the fault, so
            // the ancestors stop waiting and the failure is reported, instead of a silent park with
            // no further line of its own.
            if (advance)
                PostDisposeHostedHubsPhase();
        }
    }

    /// <summary>
    /// Posts the DisposeHostedHubs phase once quiescing is done — the Quiescing→DisposeHostedHubs
    /// half of what <see cref="PostShutDownPhase"/> does for the next transition, and wrapped for
    /// the same reason (#3593).
    ///
    /// <para>🚨 This used to be a bare <c>Post</c> in the Quiescing branch's <c>finally</c>. The
    /// <c>catch</c> a few lines above it exists because a throw in that branch <i>"would wedge the
    /// dispose state machine at Quiescing forever (worse than the original hang)"</i> — and the
    /// <c>Post</c> sat outside it, so the single statement that comment is about was the one
    /// statement unprotected. A lost Post leaves the hub at <c>Quiescing</c> with an EMPTY queue, an
    /// idle pump and <c>Disposal=Pending</c>, emitting no further line of its own: no queued
    /// request, nothing running, and every ancestor blocked behind it in DisposeHostedHubs until an
    /// outer bound ends the process.</para>
    ///
    /// <para>Force-faulting is the opposite of swallowing: <see cref="SignalDisposalFaulted"/>
    /// terminates <see cref="DisposalCompleted"/> with the fault, so the waiters above are released
    /// and the failure is REPORTED rather than becoming a silent permanent park.</para>
    /// </summary>
    private void PostDisposeHostedHubsPhase()
    {
        try
        {
            RequirePhaseAccepted(
                Post(new ShutdownRequest(MessageHubRunLevel.DisposeHostedHubs, Version)),
                MessageHubRunLevel.DisposeHostedHubs);
        }
        catch (Exception postEx)
        {
            TryLog(LogLevel.Warning, postEx,
                "[POSTED-DISPOSE-HOSTED-FAILED] {Address}: posting the DisposeHostedHubs request faulted — "
                + "the disposal state machine cannot advance out of Quiescing on its own, so disposal is "
                + "force-faulted instead of parking here forever.",
                Address);
            SignalDisposalFaulted(postEx);
        }
    }

    /// <summary>
    /// 🚨 <b>A phase request can be REFUSED without throwing, and a discarded return is the same
    /// silent park as an uncaught throw</b> (Copilot review, #4931). <c>MessageService.Post</c> has
    /// three paths that hand back a non-accepted delivery rather than raising: the shutting-down arm
    /// (<c>Failed(…, ErrorType.ShuttingDown)</c> / <c>FailedAndNacked</c>), the storm
    /// circuit-breaker and the aggregate shedder (both <c>Ignored()</c>). All three exempt
    /// lifecycle traffic today, so this is a guard rather than a live bug — but "it cannot happen"
    /// is exactly the reasoning that left the Post unwrapped to begin with.
    ///
    /// <para>Throwing here is deliberate: the caller already force-faults disposal on a throw, so
    /// the refusal joins the path that RELEASES the waiters instead of inventing a second one.</para>
    /// </summary>
    /// <param name="posted">What <c>Post</c> handed back.</param>
    /// <param name="phase">The phase that was being requested, for the message.</param>
    private void RequirePhaseAccepted(IMessageDelivery? posted, MessageHubRunLevel phase)
    {
        // Submitted is the accepted outcome for a self-posted phase request; Processed/Forwarded are
        // accepted too. Everything else means nothing is queued and nobody will advance the phase.
        if (posted is null
            || posted.State is MessageDeliveryState.Submitted
                or MessageDeliveryState.Processed
                or MessageDeliveryState.Forwarded)
            return;
        throw new InvalidOperationException(
            $"Hub {Address}: the {phase} phase request was not accepted — Post returned "
            + $"State={posted.State}. Nothing is queued, so no turn will advance the disposal state "
            + "machine; disposal is force-faulted rather than parked at this phase forever.");
    }

    /// <summary>
    /// Posts the ShutDown phase once the hosted hubs have drained. Wrapped so that a failed
    /// Post (hub in an unexpected state) still force-faults disposal rather than wedging the
    /// state machine — subscribers to <see cref="DisposalCompleted"/> never hang.
    /// </summary>
    private void PostShutDownPhase(Stopwatch sw)
    {
        try
        {
            TryLog(LogLevel.Debug, "[DISPOSE-TRACE] {address}: POSTING ShutDown request, Version={version}",
                Address, Version);
            // Same refusal check as the sibling above: this one caught a THROW and discarded the
            // returned state, so a refused ShutDown request parked the hub at DisposeHostedHubs
            // exactly as a refused DisposeHostedHubs request parked it at Quiescing (#4931).
            RequirePhaseAccepted(
                Post(new ShutdownRequest(MessageHubRunLevel.ShutDown, Version)),
                MessageHubRunLevel.ShutDown);
        }
        catch (Exception postEx)
        {
            TryLog(LogLevel.Warning, postEx,
                "[POSTED-SHUTDOWN-FAILED] {Address}: posting the ShutDown request faulted after {Elapsed}ms",
                Address, sw.ElapsedMilliseconds);
            SignalDisposalFaulted(postEx);
        }
    }

    /// <summary>
    /// Best-effort logger call that swallows any exception. The dispose pipeline
    /// runs while DI scope / logger may be partially torn down (e.g. tests dispose
    /// the service provider before the hub's disposal task completes); a logger
    /// call that throws during dispose would otherwise wedge the state machine.
    /// Field <see cref="logger"/> is non-nullable but the underlying logger
    /// factory may already be disposed — the null-conditional + catch is what
    /// keeps the state machine progressing in that case.
    /// </summary>
    private void TryLog(LogLevel level, string message, params object?[] args)
    {
        try
        {
            logger?.Log(level, message, args);
        }
        catch
        {
            // Swallow — diagnostic logging must never throw out of the dispose path.
        }
    }

    /// <summary>
    /// <see cref="TryLog(LogLevel, string, object?[])"/> carrying the causing exception, so a
    /// fault raised inside the dispose pipeline keeps its stack trace instead of being flattened
    /// into the message. Same best-effort contract: never throws out of the dispose path.
    /// </summary>
    private void TryLog(LogLevel level, Exception exception, string message, params object?[] args)
    {
        try
        {
            logger?.Log(level, exception, message, args);
        }
        catch
        {
            // Swallow — diagnostic logging must never throw out of the dispose path.
        }
    }

    private void CancelCallbacks()
    {
        // Push ObjectDisposedException to all pending response subjects so anyone
        // currently subscribed gets onError instead of waiting forever.
        KeyValuePair<string, PendingCallback>[] pending;
        lock (responseSubjects)
        {
            pending = responseSubjects.ToArray();
            responseSubjects.Clear();
            // Callers are being errored out — nothing awaits these ids any more. Drop their trails
            // (the quiescing detail has already been rendered from them by the caller above).
            foreach (var (messageId, _) in pending)
                requestFates.Untrack(messageId);
        }
        logger.LogDebug("Cancelling {SubjectCount} pending response subjects during disposal for hub {Address}",
            pending.Length, Address);

        foreach (var (_, entry) in pending)
        {
            try
            {
                // 🚨 TYPED, so callers can classify it (#3148). This is a teardown fact — the hub
                // was recycled or deactivated with the request outstanding — not a fault of the
                // work that hit it, and until it had a type the only way to tell the two apart was
                // to match this message. The message itself is unchanged on purpose; see
                // HubDisposedBeforeResponseException.
                entry.Subject.OnError(new HubDisposedBeforeResponseException(
                    nameof(MessageHub), Address, entry.RequestType, entry.Target?.ToString()));
            }
            catch
            {
                // Subject may already be terminated — ignore.
            }
        }
    }

    /// <summary>
    /// Snapshot of currently-pending response callbacks. Used by the Quiescing dispose
    /// phase and by <see cref="GetDisposalDiagnostics"/> so a hung dispose names *what*
    /// the hub was waiting on, not just that it was waiting.
    /// </summary>
    private (string MessageId, string RequestType, Address? Target, long AgeMs, string? DiagnosticKey)[]
        SnapshotPendingCallbacks()
    {
        lock (responseSubjects)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            return responseSubjects
                .Select(kv => (
                    kv.Key,
                    kv.Value.RequestType,
                    kv.Value.Target,
                    (long)((nowTicks - kv.Value.RegisteredAtTicks) * 1000.0 / Stopwatch.Frequency),
                    kv.Value.DiagnosticKey))
                .ToArray();
        }
    }

    private const int PendingCallbackLogCap = 20;

    private static string FormatPendingCallbacks(
        (string MessageId, string RequestType, Address? Target, long AgeMs, string? DiagnosticKey)[] pending)
    {
        if (pending.Length == 0)
            return "<none>";
        // Cap individual-callback enumeration — a stuck hub with 995 outstanding
        // DataChangeRequests was emitting a single ~100KB log line that broke
        // downstream TRX parsers (`xmlSAX2Characters: huge text node`). The first
        // few + a per-(RequestType,Target) tally is enough to diagnose; anything
        // beyond is noise that drowns the rest of the log.
        if (pending.Length <= PendingCallbackLogCap)
        {
            return string.Join(", ", pending.Select(p =>
                $"{p.MessageId}={p.RequestType}@{p.Target}({p.AgeMs}ms)"));
        }
        var head = string.Join(", ", pending.Take(PendingCallbackLogCap).Select(p =>
            $"{p.MessageId}={p.RequestType}@{p.Target}({p.AgeMs}ms)"));
        // 🚨 The tally groups by (type, target) — which on memex-cloud 2026-08-12 collapsed 167
        // pending SubscribeRequests into one indistinguishable bucket. `keys=` is what tells the two
        // mechanisms apart: keys≈count ⇒ that many SEPARATE streams (a fan-out); keys=1 ⇒ one stream
        // re-asking (a retry loop). See IDiagnosticKeyed. Groups whose requests carry no key print
        // no `keys=`, so nothing changes for message types that opt out.
        var rest = PendingCallbackReport.Tally(pending.Skip(PendingCallbackLogCap)
            .Select(p => new PendingCallbackInfo(p.RequestType, p.Target?.ToString(), p.DiagnosticKey)));
        return $"{head}, …+{pending.Length - PendingCallbackLogCap} more [{rest}]";
    }

    /// <summary>
    /// Renders the HANDLER-side trail (see <see cref="RequestFateLedger"/>) for each still-pending
    /// callback, one indented line per request id.
    ///
    /// <para>This is the half that every #981 capture was missing. <see cref="FormatPendingCallbacks"/>
    /// answers "what did this hub post and never hear back about"; this answers "and what happened
    /// to that delivery" — received / routed / deferred / dropped / handled / answered — which is
    /// what tells a leaked subscription apart from a request the receiver never saw, never handled,
    /// or handled without replying.</para>
    ///
    /// <para>Capped by the same <see cref="PendingCallbackLogCap"/> as the caller-side line, for
    /// the same reason: one enormous log line breaks downstream TRX parsing.</para>
    /// </summary>
    /// <param name="pending">The still-pending callbacks, as snapshotted at the timeout.</param>
    /// <returns>A newline-prefixed block, or the empty string when there is nothing to report.</returns>
    private string FormatPendingCallbackFates(
        (string MessageId, string RequestType, Address? Target, long AgeMs, string? DiagnosticKey)[] pending)
    {
        if (pending.Length == 0)
            return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append(Environment.NewLine).Append("  handler-side fate (what happened to the delivery):");
        foreach (var p in pending.Take(PendingCallbackLogCap))
        {
            sb.Append(Environment.NewLine).Append("    ").Append(p.MessageId).Append('=')
              .Append(p.RequestType).Append(": ").Append(requestFates.Describe(p.MessageId));
            // 🚨 The TARGET's pump, as it is NOW. A trail that ends `RECEIVED → ENQUEUED → QUEUED
            // depth=1` at the target and then nothing says the target never dequeued it — and
            // the one thing that decides between "a turn is parked there" (its name and age),
            // "a drain is scheduled and not running" (the scheduler holds it) and "nothing is
            // outstanding" (the latch invariant broken) is the target's own queue snapshot, which
            // the requester's report never carried. Measured 2026-09-13 on the Plugins gate
            // (#2543 / #4141): 25 stale callbacks, every SubscribeRequest one of them ending
            // exactly that way at a NodeType hub, `pendingWork=316` on the pool, and no line
            // anywhere naming what that hub was doing.
            var pump = DescribeLocalPump(p.Target);
            if (pump is not null)
                sb.Append(Environment.NewLine).Append("      target pump now: ").Append(pump);
        }
        if (pending.Length > PendingCallbackLogCap)
            sb.Append(Environment.NewLine).Append("    …+")
              .Append(pending.Length - PendingCallbackLogCap).Append(" more not rendered");
        return sb.ToString();
    }

    /// <summary>
    /// The pump state of the in-process hub at <paramref name="target"/> — run level, the turn
    /// executing now and for how long, queue depths, drains in flight and drains the scheduler
    /// still holds — or <c>null</c> when the target is not a hub under this tree's root. Read-only
    /// and lock-free: the same snapshot the disposal diagnostics print for a hub's own queue,
    /// taken here for the hub a pending callback is waiting ON.
    /// </summary>
    private string? DescribeLocalPump(Address? target)
    {
        if (target is null)
            return null;
        try
        {
            IMessageHub root = this;
            while ((root as MessageHub)?.messageService is MessageService ms && ms.ParentHub is { } parent)
                root = parent;
            var levels = ImmutableList<Address>.Empty;
            for (var level = target; level is not null; level = level.Host)
                levels = levels.Insert(0, level with { Host = null });
            var hub = ResolveTopLevelHub(root, levels[0]);
            for (var i = 1; hub is not null && i < levels.Count; i++)
                hub = hub.GetHostedHub(levels[i], HostedHubCreation.Never);
            if (hub is not MessageHub { messageService: MessageService service } local)
                return null;
            var q = service.GetQueueSnapshot();
            var turn = q.CurrentMessage is null
                ? "idle"
                : $"turn={q.CurrentMessage} running {q.CurrentMessageElapsedMs}ms";
            return $"{local.Address} RunLevel={local.RunLevel} {turn} buffer={q.Buffer} deferred={q.Deferred} "
                 + $"drainsInFlight={q.DrainsInFlight} awaitingScheduler={q.DrainsAwaitingScheduler} "
                 + $"draining={q.Draining} openGates={q.OpenGates}";
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-hub Quiescing-phase budget. Configured via
    /// <see cref="MessageHubConfiguration.WithQuiesceTimeout"/>; defaults to 2 s.
    /// Tests with deliberately abandoned <c>Observe(...)</c> subscriptions should
    /// drop this to ~100-500 ms.
    /// </summary>
    private TimeSpan QuiesceTimeout => Configuration.QuiesceTimeout;
    private static readonly TimeSpan QuiescePollInterval = TimeSpan.FromMilliseconds(50);


    // Every SYNCHRONOUS registered cleanup — subscriptions, Action<IMessageHub>
    // lambdas, the pool-bridged IDisposable a routing service hands back — lives here.
    // Disposed SYNCHRONOUSLY in the ShutDown phase (DisposeImpl). This is "normal
    // subscription logic": no ConcurrentBag of callbacks, no imperative drain. A
    // CompositeDisposable also disposes any registrant added after it is itself
    // disposed, so late registrations during teardown can't leak.
    private readonly System.Reactive.Disposables.CompositeDisposable disposables = new();

    // REACTIVE dispose actions (Func returning IObservable<Unit>) — composed into one
    // chain and subscribed at dispose so their async leaves run on the mesh IO pool
    // (see DisposeImpl). Immutable list, NOT a ConcurrentBag; mutated under a lock.
    private ImmutableList<Func<IMessageHub, IObservable<Unit>>> reactiveDisposeActions =
        ImmutableList<Func<IMessageHub, IObservable<Unit>>>.Empty;
    private readonly object reactiveDisposeLock = new();



    private readonly ConcurrentDictionary<(string Conext, Type Type), object?> properties = new();

    /// <summary>
    /// Stores a value in the per-hub property bag, keyed by (<paramref name="context"/>, typeof(T)).
    /// Caches instance state on the hub without a static dictionary; the entry lives with the hub.
    /// </summary>
    /// <typeparam name="T">The value type, part of the bag key.</typeparam>
    /// <param name="obj">The value to store.</param>
    /// <param name="context">An optional discriminator allowing multiple entries of the same type.</param>
    public void Set<T>(T obj, string context = "")
    {
        properties[(context, typeof(T))] = obj;
    }

    /// <summary>
    /// Reads the value previously stored via <see cref="Set{T}"/> for (<paramref name="context"/>, typeof(T)).
    /// </summary>
    /// <typeparam name="T">The value type, part of the bag key.</typeparam>
    /// <param name="context">The discriminator used when the value was stored.</param>
    /// <returns>The stored value, or <c>default</c> when no entry exists.</returns>
    public T Get<T>(string context = "")
    {
        properties.TryGetValue((context, typeof(T)), out var ret);
        return (T)ret!;
    }


    private IMessageDelivery HandleDispose(IMessageDelivery<DisposeRequest> request)
    {
        // 🚨 The root mesh hub (Address.Type == "mesh") is an irreplaceable,
        // process-lifetime DI singleton — built ONCE via AddSingleton(BuildHub) and
        // never rebuilt. DisposeRequest is a [SystemMessage] with NO permission gate,
        // so ANY sender (including an unauthenticated external / RawJson client routed
        // to mesh/<id>) could otherwise tear the whole mesh down: once disposed, every
        // node operation times out at 60 s forever until the process restarts — the
        // prod mesh-wide outage on 2026-06-10. The host owns this hub's lifecycle and
        // disposes it via a DIRECT Dispose() call (MeshTeardownExtensions.TeardownAsync
        // on host shutdown), NEVER through the message bus — so refusing the message
        // path here cannot block a legitimate shutdown. Per-node / portal / client hubs
        // stay message-disposable (recycle, circuit teardown).
        if (Address.Type == AddressExtensions.MeshType)
        {
            logger.LogWarning(
                "Refused a message-routed DisposeRequest targeting the root mesh hub {Address} (sender={Sender}). " +
                "The mesh hub's lifecycle is owned by host teardown, not the message bus.",
                Address, request.Sender);
            return request.Ignored();
        }

        // 🚨 A RECYCLE MUST REACH THE SUBSCRIBERS IT IS ABOUT TO ORPHAN — and this turn is the
        // only place it can (Systemorph/MeshWeaver#2533 / #2551).
        //
        // A routed DisposeRequest is a RECYCLE: the address is coming back, and the whole point of
        // the automatic ones (NodeTypeEnrichmentHelpers.WithOverlaySelfHeal, NodeTypeRebindWatcher,
        // the stale-build convergence branch) is to get LIVE viewers off a degraded page. But the
        // teardown itself is silent by construction: JsonSynchronizationStream's StreamEndedEvent
        // announcement is deliberately suppressed once the owning hub is disposing ("a hub must
        // speak only for itself, and never while it is dying" — a dying owner reaching up the hub
        // tree for a last word RESURRECTS the Orleans activation it is retiring). It delegates the
        // teardown case to the recycle re-arm and the change-feed latch, and NEITHER can fire here:
        // the re-arm needs an in-flight SubscribeRequest to be NACKed, and the latch needs a WRITE
        // — and, as the same file says elsewhere, "a recycle IS NOT A WRITE". So the self-heal was
        // tearing the hub down under the exact audience it exists for, and every one of them held
        // its last frame (the compile-progress overlay) until the page was reloaded. On a
        // framework-identity bump that is every instance hub in the fleet at once.
        //
        // Hence: announce FIRST, while the hub is whole — the workspace's client subscription
        // registry is intact and the parent hub is resolvable. The announcement implementation
        // captures what it needs now and delivers AFTER DisposalCompleted through a carrier that
        // outlives this hub, so nothing is posted from a dying hub and no re-ask races the teardown
        // it is a response to.
        //
        // 🚨 …and the announcement is NOT made here (#3986). It is made at the top of
        // <see cref="Dispose()"/> — see AnnounceRecycleUnlessAnAncestorIsTakingUsWithIt — because
        // "a routed DisposeRequest" is the wrong discriminator for "this address is coming back".
        // Orleans DEACTIVATION is a direct Dispose() of an address that IS coming back, and it is
        // the largest single source of direct Dispose() in the mesh (MessageHubGrain.OnDeactivateAsync,
        // #4888): keyed on the routed request, a deactivating owner told its live subscribers
        // NOTHING. HandleDispose still ends in Dispose(), on this same turn and with the hub still
        // whole, so the routed recycle announces exactly as before.
        //
        // 🚨 Read ONCE. The same fact answers two questions — may this recycle cascade, and is this
        // request the CAUSE of the teardown — and two reads of a flag another thread can flip would
        // let them disagree.
        var startsTheTeardown = !IsShuttingDown;

        // The dependency network first, while this hub is still whole enough to compute it: the
        // set is derived HERE, once, and delivered by a surviving hub. A request that is itself a
        // cascade never fans out again (RecycleCascade / DisposeRequest.CascadedFrom).
        if (startsTheTeardown && request.Message.CascadedFrom is null)
            CascadeRecycle(request.Message);

        // Recorded BEFORE Dispose(), because Dispose() is what logs [QUIESCE-START] (#3510). Set
        // here rather than at the top of the handler so it means what it says: this request was
        // honoured, not merely received — the root-mesh refusal above returns without disposing.
        //
        // 🚨 SELF-POSTED is called out, not printed as an address. The automatic recycles post to
        // their OWN hub (NodeTypeRebindWatcher, WithOverlaySelfHeal), so the bare sender would read
        // "[QUIESCE-START] Hosting: requested by Hosting" — true, useless, and easy to misread as a
        // routing oddity. The reader's question is which of three things happened, so the line says
        // which: a recycle this hub asked for, a teardown someone else asked for, or no message at
        // all. #3510's leading hypothesis is precisely the first, and the installer
        // (PackageInstaller posting to a package root) is the second.
        // 🚨 WHO is only half of it. The self-posted reading below is ONE WORD COVERING THREE
        // STATES — NodeTypeRebindWatcher, the stale-build convergence and WithOverlaySelfHeal all
        // post to their own hub — and #3510's leading hypothesis was precisely "which of those
        // was it?". The poster always knew; the request had nowhere to carry it. It does now, and
        // an omission is reported as an omission rather than as silence.
        //
        // 🚨 ONLY IF THIS REQUEST IS ACTUALLY THE CAUSE. Two conditions, and both are needed:
        //
        //   `startsTheTeardown` — a teardown already under way was started by somebody else (a
        //     direct Dispose() from host teardown or a `using`, or an owner's cascade). This
        //     request did not cause it, and claiming it would replace a TRUE reading
        //     (DirectDisposeSource, which rules the message path out) with a false one. Reachable:
        //     Dispose() sets IsShuttingDown and only then posts ShutdownRequest(Quiescing), so a
        //     DisposeRequest arriving in that window still sees RunLevel=Started, is admitted by
        //     RefusesIntake, and its turn runs after the teardown has begun.
        //
        //   `TryPublishTeardownCause()` — the atomic half, against NoteCascadeFrom and
        //   NoteDirectDisposalBy on other threads. First cause wins; the cause is complete
        //   before it is visible.
        //
        // What remains is a genuinely SIMULTANEOUS pair — a direct Dispose() and a routed request
        // landing within the same instant — where both really happened and either attribution is
        // true. That is the honest residue; it is not an overwrite of an earlier cause.
        if (startsTheTeardown)
        {
            // 🚨 A BLANK REASON IS AN UNSTATED ONE. `DisposeRequest.Reason` is free text from the
            // poster, and `null` was the only value the renderers treated as "not stated" — so an
            // empty or whitespace string produced a literal `why: ` with nothing after it, which
            // is precisely the "renders as nothing, reads as nothing to report" failure this whole
            // change exists to remove. Normalised HERE, at the single capture point, so every
            // reader (DisposalReason AND DisposalOriginForChildren) inherits it instead of each
            // having to remember.
            //
            // Built in full BEFORE publication: the cause becomes visible as one reference, so no
            // reader can catch it half-written (#4888 review).
            TryPublishTeardownCause(new TeardownCause(
                RequestedBy: request.Sender is null
                    ? "an unnamed sender (routed DisposeRequest)"
                    : Equals(request.Sender, Address)
                        ? $"itself — a self-posted DisposeRequest ({request.Sender}), i.e. a rebind or "
                          + "self-heal recycle"
                        : $"{request.Sender} (routed DisposeRequest)",
                Reason: string.IsNullOrWhiteSpace(request.Message.Reason)
                    ? null
                    : request.Message.Reason,
                CascadeOwner: null));
        }

        Dispose();
        return request.Processed();
    }

    /// <summary>
    /// Hands the <see cref="RecycleAnnouncement"/> hung on this hub (if any) its one turn — see
    /// that type for the contract. Best-effort by design: a recycle that cannot announce must
    /// still recycle, so a faulting announcement is logged and never propagated into the teardown.
    /// </summary>
    private void CascadeRecycle(DisposeRequest request)
    {
        try
        {
            Get<RecycleCascade>()?.Cascade(request);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Recycle cascade for hub {Address} faulted — its dependency network keeps its live "
                + "activations until each is recycled by hand",
                Address);
        }
    }

    /// <summary>
    /// Gives this hub's live subscribers their ONE goodbye, when this teardown is this hub's OWN —
    /// the <see cref="RecycleAnnouncement"/> seam, keyed on the fact that actually decides whether
    /// telling them to re-ask is right (issue #3986).
    ///
    /// <para>🚨 <b>"Routed <c>DisposeRequest</c>" was the wrong discriminator, and the route it
    /// missed is the commonest one there is.</b> The seam was introduced for the automatic recycles
    /// (#2533 / #2551) and hung on <see cref="HandleDispose"/>, on the stated reasoning that a routed
    /// request means "the address is coming back" while a direct <c>Dispose()</c> means "the whole
    /// tree is going down". The second half is false for <c>MessageHubGrain.OnDeactivateAsync</c> —
    /// which this codebase calls "the largest single source of direct <c>Dispose()</c> in the mesh"
    /// (#4888) — where the address IS coming back (Orleans reactivates it on the next message) and the
    /// subscribers are NOT going down with it: they are live mirrors in other hubs, other circuits,
    /// other pods. So an owner grain that deactivated told nobody, and because
    /// <c>JsonSynchronizationStream</c>'s per-stream <c>StreamEndedEvent</c> is deliberately
    /// suppressed once the owning hub is disposing, nothing else spoke either. The subscriber kept
    /// replaying its last snapshot — the page still rendered — and every user action it sent
    /// afterwards was refused "NO sync hub for this stream was EVER registered on the current
    /// activation" and thrown away. Measured in production on memex-cloud 2026-09-18: four clicks on
    /// <c>Catalog/Categories/Cat-Education</c> from ONE still-live sender over twelve seconds, all
    /// refused (issue #3986, occurrences 5–8).</para>
    ///
    /// <para><b>The fact that actually decides it is whether a CARRIER OUTLIVES US</b>, and it is
    /// answered in two independent places, neither of them a guess:</para>
    /// <list type="bullet">
    ///   <item><description>HERE: <see cref="IsShuttingDown"/> is already true when an ANCESTOR is
    ///     taking us with it — <c>HostedHubsCollection.CloseCreation</c> freezes the whole subtree
    ///     the instant the ancestor's own <c>Dispose()</c> starts, strictly before it disposes its
    ///     children. Then the carrier (our parent) is going down too, the address is NOT coming
    ///     back, and telling subscribers to re-ask is exactly the resurrection
    ///     <c>JsonSynchronizationStream</c>'s suppression exists to prevent. Silent, as before.</description></item>
    ///   <item><description>In the announcement itself: <c>Workspace.AnnounceRecycleToClientSubscriptions</c>
    ///     resolves a non-router carrier that outlives this hub and returns without posting when
    ///     there is none — which is what keeps a ROOT hub's host teardown silent (its parent
    ///     resolves to itself) without this method having to know about hosts at all.</description></item>
    /// </list>
    ///
    /// <para>Exactly ONE goodbye per hub: more than one multiplies the bounded re-ask a subscriber
    /// answers with (<c>JsonSynchronizationStream</c>'s recycle re-arm), and the routed path reaches
    /// this through <see cref="HandleDispose"/>'s own call to <see cref="Dispose"/>, on the same turn
    /// it used to announce from.</para>
    /// </summary>
    private void AnnounceRecycleUnlessAnAncestorIsTakingUsWithIt()
    {
        if (IsShuttingDown)
            return;
        // One-shot against two concurrent Dispose() callers: both can read IsShuttingDown as false
        // before either sets disposalStarted, and the idempotency guard inside Dispose() is taken
        // after this point.
        if (Interlocked.Exchange(ref recycleAnnounced, 1) != 0)
            return;
        AnnounceRecycle();
    }

    /// <summary>0 until this hub's one recycle announcement has been handed its turn.</summary>
    private int recycleAnnounced;

    private void AnnounceRecycle()
    {
        try
        {
            Get<RecycleAnnouncement>()?.Announce();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Recycle announcement for hub {Address} faulted — its live subscribers fall back to "
                + "the pre-fix behaviour (they recover on the owner's next write, or on reload)",
                Address);
        }
    }


    #region Registry
    /// <summary>Registers a synchronous handler for messages of type <typeparamref name="TMessage"/> (no filter).</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(SyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    /// <summary>Registers an asynchronous (observable-returning) handler for messages of type <typeparamref name="TMessage"/> (no filter).</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(AsyncDelivery<TMessage> action) =>
        Register(action, _ => true);

    /// <summary>Registers a synchronous handler for messages of type <typeparamref name="TMessage"/> that pass <paramref name="filter"/>.</summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        return Register((d, _) => Observable.Return(action(d)), filter);
    }

    /// <summary>
    /// Registers an asynchronous handler that is INHERITED — appended to the END of the rule chain (so
    /// it runs after the hub's own rules), passing through deliveries that don't match
    /// <typeparamref name="TMessage"/> or the optional <paramref name="filter"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives; <c>null</c> matches all of the type.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    )
    {
        var node = new LinkedListNode<AsyncDelivery>(
            (d, c) =>
                d is IMessageDelivery<TMessage> md && (filter?.Invoke(md) ?? true)
                    ? action(md, c)
                    : Observable.Return(d)
        );
        rules.AddLast(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    /// <summary>
    /// Registers a non-generic synchronous handler that receives every delivery (it inspects the
    /// message type itself).
    /// </summary>
    /// <param name="delivery">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(SyncDelivery delivery) =>
        Register((d, _) => Observable.Return(delivery(d)));

    /// <summary>
    /// Registers a non-generic asynchronous handler at the FRONT of the rule chain; it receives every
    /// delivery and returns an observable of the transformed delivery.
    /// </summary>
    /// <param name="delivery">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(AsyncDelivery delivery)
    {
        var node = new LinkedListNode<AsyncDelivery>(delivery);
        rules.AddFirst(node);
        return new AnonymousDisposable(() => rules.Remove(node));
    }

    /// <summary>
    /// Synchronous overload of the inherited registration: registers an end-of-chain handler for
    /// messages of type <typeparamref name="TMessage"/> matching the optional <paramref name="filter"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Optional predicate selecting which deliveries the handler receives; <c>null</c> matches all of the type.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage>? filter = null
    ) => RegisterInherited((d, _) => Observable.Return(action(d)), filter);

    /// <summary>
    /// Registers an asynchronous handler for messages of type <typeparamref name="TMessage"/> that
    /// target this hub's address and pass <paramref name="filter"/>. Also registers the message type
    /// (and related types) in the type registry.
    /// </summary>
    /// <typeparam name="TMessage">The message type the handler processes.</typeparam>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter
    )
    {
        WithTypeAndRelatedTypesFor(typeof(TMessage));
        return Register(
            (d, c) => action((IMessageDelivery<TMessage>)d, c),
            d =>
            {
                // Compare without Host since Host tracks routing path
                var targetWithoutHost = d.Target is not null ? d.Target with { Host = null } : null;
                return (targetWithoutHost == null || Address.Equals(targetWithoutHost)) && d is IMessageDelivery<TMessage> md && filter(md);
            }
        );
    }

    /// <summary>
    /// Registers a non-generic asynchronous handler for messages assignable to <paramref name="tMessage"/>,
    /// registering that type in the type registry.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The reactive delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, AsyncDelivery action)
    {
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(action, d => tMessage.IsInstanceOfType(d.Message));
    }

    /// <summary>
    /// Registers a non-generic asynchronous handler at the FRONT of the rule chain, invoked only for
    /// deliveries that pass <paramref name="filter"/> (non-matching deliveries pass through unchanged).
    /// </summary>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(AsyncDelivery action, DeliveryFilter filter)
    {
        IObservable<IMessageDelivery> Rule
            (IMessageDelivery delivery, CancellationToken cancellationToken)
            => WrapFilter(delivery, action, filter, cancellationToken);
        var node = new LinkedListNode<AsyncDelivery>(Rule);
        rules.AddFirst(node);
        return new AnonymousDisposable(() =>
        {
            rules.Remove(node);
        });
    }

    private IObservable<IMessageDelivery> WrapFilter(
        IMessageDelivery delivery,
        AsyncDelivery action,
        DeliveryFilter filter,
        CancellationToken cancellationToken
    )
    {
        if (filter(delivery))
            return action(delivery, cancellationToken);
        return Observable.Return(delivery);
    }

    /// <summary>
    /// Registers a non-generic synchronous handler for messages assignable to <paramref name="tMessage"/> (no filter).
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, SyncDelivery action) =>
        Register(tMessage, action, _ => true);

    /// <summary>
    /// Registers a non-generic synchronous handler for messages assignable to <paramref name="tMessage"/>
    /// that also pass <paramref name="filter"/>.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The synchronous delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter) =>
        Register(
            tMessage,
            (d, _) =>
            {
                d = action(d);
                return Observable.Return(d);
            },
            filter
        );


    /// <summary>
    /// Registers a non-generic asynchronous handler for messages assignable to <paramref name="tMessage"/>
    /// that also pass <paramref name="filter"/>, registering the type (and related types) in the type registry.
    /// </summary>
    /// <param name="tMessage">The message type (by runtime <see cref="Type"/>) the handler processes.</param>
    /// <param name="action">The reactive delivery handler.</param>
    /// <param name="filter">Predicate selecting which deliveries the handler receives.</param>
    /// <returns>A disposable that unregisters the handler.</returns>
    public IDisposable Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter)
    {
        WithTypeAndRelatedTypesFor(tMessage);
        return Register(
            (d, c) => action(d, c),
            d => tMessage.IsInstanceOfType(d.Message) && filter(d)
        );
    }
    #endregion
}
