using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// <see cref="ILogger"/> implementation that appends each log call to the
/// <c>Messages</c> list of a target <c>ActivityLog</c> MeshNode, through the
/// canonical <c>GetMeshNodeStream(path).Update(...)</c> mutation API — so the
/// owning hub's workspace stream ticks and subscribers
/// (<c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>) receive live
/// message updates.
///
/// <para>🚨 <b>Never a hand-posted <c>DataChangeRequest</c> carrying a whole
/// MeshNode.</b> That write lands in the workspace VERBATIM — the version
/// stamping in <c>MeshNodeTypeSource.UpdateImpl</c> is side-effect-only and never
/// reaches the store — so the activity node sat at <c>Version = 0</c> for an
/// entire run. The persistence sampler then normalises <c>0 → 1</c> when it
/// writes the row (<c>HandleSaveMeshNode</c>), and the storage change feed echoes
/// that row back at Version 1. Both of the owner's defences read the version and
/// therefore both were defeated: the echo-suppression in
/// <c>SubscribeToOwnDeletion</c> compares <c>persisted.Version (1)</c> against
/// the live <c>Version (0)</c> and sees no self-write, and
/// <c>MeshNodeStreamHandle.AdoptPersisted</c>'s forward-only guard reads
/// <c>1 &gt; 0</c> as "strictly newer" and adopts it. A snapshot sampled MID-RUN
/// then landed on top of the terminal write and rolled a Succeeded activity back
/// to <c>Running</c> with <c>End</c> cleared — permanently, since a finished run
/// never writes again (issue #1784; same defect family as the lagged-echo
/// data loss of #133). <c>Update</c> mints
/// <c>MeshNode.NextVersion(...)</c> on the owner, which is what makes both
/// guards work — and it patches only <c>Content</c>, so <c>MainNode</c>,
/// <c>HubPath</c>, <c>Start</c>, <c>User</c> and a pending
/// <c>RequestedStatus</c> survive instead of being clobbered by a rebuilt node.</para>
///
/// Injected into the script's <c>Log</c> global per <see cref="SubmitCodeRequest"/>
/// so every concurrent run writes to its own ActivityLog.
/// </summary>
/// <param name="hub">The hub that OWNS the activity node (the kernel's public hub) — its
/// workspace carries the MeshNode data source the write goes through.</param>
/// <param name="activityLogPath">Path of the activity node to append to.</param>
internal sealed class ActivityLogLogger(IMessageHub hub, string activityLogPath) : ILogger
{
    private readonly object _lock = new();
    // 🚨 Kernel activity-log publishing is INFRASTRUCTURE observability and fires from a
    // throttle TIMER thread (Observable.Timer below) that never inherited the script
    // runner's AccessContext → the DataChangeRequest post would be context-null and
    // RLS-denied on the activity's partition → the activity log never ticks. Publish
    // under System (Permission.All) — same rule as compile (#2) / user-activity (#3).
    private readonly AccessService? _accessService = hub.ServiceProvider.GetService<AccessService>();
    private readonly ILogger? _diagnostics = hub.ServiceProvider.GetService<ILoggerFactory>()
        ?.CreateLogger("MeshWeaver.Kernel.ActivityLogLogger");
    // Resolved lazily on the first publish: the handle needs the hub's workspace, which is not
    // guaranteed to be built while the hub itself is being configured.
    private MeshNodeStreamHandle? _stream;
    private ImmutableList<LogMessage> _messages = ImmutableList<LogMessage>.Empty;
    // Incremental severity roll-up, so the published log's terminal status never depends on how much
    // of the transcript the head window still holds.
    private LogLevel _maxSeverity = LogLevel.Trace;

    // 🚨 Window state — read and written ONLY under _publishLock, alongside the publish itself.
    // This logger is the SINGLE writer of its activity node (it re-asserts whole content on every
    // flush rather than patching), so it seals its own overflow directly and needs none of
    // ActivityLogAppender's claim protocol: there is no second appender to race.
    //
    // _sealedCount / _sealedSegments advance ONLY in the segment write's success callback. Until
    // then the messages stay in the published window, so a failed segment write costs a bigger
    // window and a retry on the next flush — never a lost line, and never a watchdog.
    private int _sealedCount;
    private int _sealedSegments;
    private bool _sealInFlight;

    // 🚨 Terminal-settle state — guarded by _publishLock, together with every
    // publish. Once Complete has stored a terminal status here, EVERY subsequent
    // snapshot (immediate append flush, throttle tail-flush timer, duplicate
    // Complete) re-asserts it. Decision + hub.Post happen under the SAME lock, so
    // post order == decision order and a Running snapshot can never be posted
    // after the terminal one. Before this, a log append arriving after Complete
    // (a late Subscribe-callback log, a leaked subscription still ticking, or the
    // tail-flush timer racing Complete's check-then-publish) re-published
    // Status=Running/End=null wholesale over a settled Failed/Succeeded — the
    // 2026-07-02/03 memex-cloud "zombie activity stuck Running forever" RCA.
    private readonly object _publishLock = new();
    private ActivityStatus? _terminalStatus;
    private DateTime? _terminalEnd;
    private JsonElement? _terminalReturnValue;

    // Rate-limit running-state publishes. Each Log call appends to _messages but
    // only triggers a DataChangeRequest at most once per ThrottleMs. Without
    // this, scripts that do heavy work — node-create churn, NodeCopy, etc. —
    // flood the activity hub's synchronization stream with concurrent patches
    // and trigger StaleStreamStateException reorderings, eventually starving
    // SubscribeRequest responses. The Complete path bypasses the throttle so
    // terminal status always lands.
    private const int ThrottleMs = 100;
    private long _lastPublishTicks;
    private int _publishScheduled;

    // 🚨 #995 — OWNERSHIP of the tail-flush timer. `Observable.Timer` parks its entry on the
    // process-wide TimerQueue, which is a strong GC root: while the timer is pending it holds
    // the tick closure → THIS logger → the primary-ctor `hub` it posts to. The old code
    // DISCARDED the Subscribe result, so nothing could cancel a flush that was still pending
    // when the hub tore down and the hub stayed rooted past its own disposal (same shape as
    // the four watcher timers fixed in #996, 10× shorter window).
    //
    // Holding it in a SerialDisposable registered on the hub closes that: hub teardown
    // disposes the composite, which cancels the pending timer. Assignment also disposes the
    // previous entry, which is either a spent (self-detached) sink or nothing — `_publishScheduled`
    // guarantees at most one timer is ever pending, so a live flush can never be clobbered.
    // The registration itself retains only this SerialDisposable (~32 B): a FIRED Rx timer
    // subscription no longer reaches its callback closure (measured), so a settled logger and
    // its message list stay collectable while the hub lives on.
    private readonly SerialDisposable pendingFlush = RegisterPendingFlush(hub);

    private static SerialDisposable RegisterPendingFlush(IMessageHub hub)
    {
        var pending = new SerialDisposable();
        hub.RegisterForDisposal(pending);
        return pending;
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var text = formatter(state, exception);
        if (exception != null)
            text = text + "\n" + exception;

        LogMessage entry;
        try
        {
            entry = new LogMessage(text, logLevel);
        }
        catch { return; }

        lock (_lock)
        {
            _messages = _messages.Add(entry);
            if (logLevel > _maxSeverity && logLevel != LogLevel.None) _maxSeverity = logLevel;
        }

        // Best-effort push, throttled. Failures never surface into the script —
        // the activity log is an observability surface, not a correctness path.
        ScheduleThrottledPublish();
    }

    /// <summary>
    /// Emits a running-state snapshot at most once every <see cref="ThrottleMs"/>.
    /// Coalesces bursts of log calls into a single DataChangeRequest so the
    /// activity hub's stream isn't flooded with concurrent patches.
    /// </summary>
    private void ScheduleThrottledPublish()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastPublishTicks);
        if (now - last < ThrottleMs)
        {
            // Schedule a tail flush: if no other thread has scheduled one, queue a
            // delayed publish so the latest snapshot still lands. 🚨 Reactive timer ONLY —
            // NEVER Task.Run/Task.Delay here. A bare Task.Run schedules an async state
            // machine on the shared ThreadPool, the SAME pool the hub turn-loop runs on
            // (TaskScheduler.Default); under a 2-core box a burst of these starves the
            // pool and the hub's own delivery continuations get queued behind them —
            // which reorders rapid same-sender posts (cell-2 overtaking cell-1) and
            // stretches a cold compile into a pseudo-deadlock. Observable.Timer is a pure
            // timer-queue one-shot: no immediate dispatch, no parked thread, no await.
            if (Interlocked.CompareExchange(ref _publishScheduled, 1, 0) == 0)
            {
                // Held, not discarded — see `pendingFlush` (#995). Once the hub is disposed the
                // SerialDisposable is disposed too, so this assignment kills the new timer on
                // the spot and a dead hub can never be woken by a tail flush.
                pendingFlush.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(ThrottleMs))
                    .Subscribe(_ =>
                    {
                        Interlocked.Exchange(ref _publishScheduled, 0);
                        Interlocked.Exchange(ref _lastPublishTicks, Environment.TickCount64);
                        // Publishing after Complete is safe — PublishSnapshot
                        // resolves the status under _publishLock and re-asserts
                        // the terminal state, so this tail flush surfaces late
                        // messages without ever regressing the settle. (The old
                        // check-then-publish here was the TOCTOU that let a
                        // Running snapshot land after the terminal write.)
                        PublishSnapshot();
                    });
            }
            return;
        }

        Interlocked.Exchange(ref _lastPublishTicks, now);
        PublishSnapshot();
    }

    /// <summary>
    /// Finalise the activity log with <paramref name="status"/> and flush a last
    /// update so subscribers see the terminal state. Optionally records the
    /// script's <paramref name="returnValue"/> on the activity content so request
    /// handlers that triggered the script (e.g. <c>ExportDocumentHandler</c>) can
    /// deserialize it on terminal status without a side-channel MeshNode.
    /// Idempotent — the first terminal status wins; later calls are no-ops.
    /// </summary>
    public void Complete(ActivityStatus status, JsonElement? returnValue = null)
    {
        lock (_publishLock)
        {
            if (_terminalStatus is not null) return;
            _terminalStatus = status;
            _terminalEnd = DateTime.UtcNow;
            _terminalReturnValue = returnValue;
            PublishSnapshotLocked();
        }
    }

    private void PublishSnapshot()
    {
        lock (_publishLock) PublishSnapshotLocked();
    }

    /// <summary>
    /// Builds and writes a snapshot of the current message list. MUST be called
    /// under <see cref="_publishLock"/>: the status decision (terminal vs Running)
    /// and the write are one atomic step, so write order equals decision
    /// order — once <see cref="Complete"/> has stored the terminal state, no
    /// Running-status snapshot can ever be written after it, and every later
    /// append-flush re-asserts the terminal Status/End/ReturnValue instead of
    /// clobbering them. The write is enqueued on the owning stream's hub
    /// synchronously at Subscribe (no await, no hub-turn wait), so holding the
    /// plain lock across it is safe and the enqueue order IS the lock order.
    /// </summary>
    private void PublishSnapshotLocked()
    {
        ImmutableList<LogMessage> snapshot;
        LogLevel maxSeverity;
        lock (_lock) { snapshot = _messages; maxSeverity = _maxSeverity; }

        try
        {
            // 🚨 Seal the overflow BEFORE building the payload. Without this, every 100 ms flush
            // re-posts the WHOLE transcript — so a script logging N lines serialises O(N²) bytes onto
            // one node, the shape that burned ~719 MB on a single memex-cloud activity. The window
            // makes each flush O(1); the sealed lines stay durable in _Log segment satellites.
            SealOverflowLocked(snapshot);
            var window = snapshot.GetRange(_sealedCount, snapshot.Count - _sealedCount);
            var messageCount = snapshot.Count;
            var segmentCount = _sealedSegments;
            var status = _terminalStatus ?? ActivityStatus.Running;
            var end = _terminalEnd;
            var returnValue = _terminalReturnValue;

            var stream = _stream ??= hub.GetWorkspace().GetMeshNodeStream(activityLogPath);
            var options = hub.JsonSerializerOptions;

            // 🚨 THE canonical mutation API — see the class remarks for why a hand-posted
            // DataChangeRequest carrying a rebuilt MeshNode is a data-loss bug here (#1784).
            // The lambda patches ONLY the fields this logger owns; everything the dispatcher
            // stamped at creation (Id, HubPath, Start, User) and anything a concurrent
            // control-plane writer set (RequestedStatus) rides through untouched.
            //
            // 🚨 A SYNCHRONOUS using — never Observable.Using / RunAsSystem here. Impersonation is
            // an AsyncLocal scope that must be opened and closed on ONE thread. Both reactive
            // shapes open it on the SUBSCRIBING thread and dispose it when the write's echo
            // arrives, i.e. on the owning stream hub's thread: the publisher keeps
            // `system-security` latched and the terminating thread is handed a foreign "previous".
            // The first publish of a run is issued from the SCRIPT's own thread (Console.WriteLine
            // → LoggerTextWriter → here, inside RunOnePass), so that latch made the rest of the
            // script run as System — a `--render` export then resolved embedded areas the
            // submitting user may not read. Measured, not reasoned: with either reactive shape
            // DocumentExportAreaAccessTest fails, with this one it passes.
            // 🚨 RunAsSystem's ContainIdentity does NOT close this hole — it restores the caller's
            // identity around NOTIFICATIONS only, never around the Subscribe that opened the scope.
            // The plain `using` is sound here because the capture is synchronous on both paths: the
            // own-node write captures inside Subscribe (Observable.Create body), and the cross-hub
            // write captures at the .Update() call — both inside this block.
            // System at all because this also fires from the throttle TIMER thread, which never
            // inherited the script runner's AccessContext; the activity log is infrastructure
            // observability, not a user write.
            using (_accessService?.ImpersonateAsSystem())
                stream.Update(node =>
                    {
                        // ContentAs, never `is ActivityLog`: a degraded JsonElement (a hub whose
                        // TypeRegistry lacks the discriminator) would make a type test null, the
                        // lambda would no-op, and the run's output would never surface.
                        var current = node.ContentAs<ActivityLog>(options, _diagnostics)
                                      ?? new ActivityLog("ScriptExecution");
                        return node with
                        {
                            Content = current with
                            {
                                Messages = window,
                                MessageCount = messageCount,
                                MaxSeverity = maxSeverity,
                                SegmentCount = segmentCount,
                                Status = status,
                                End = end,
                                // Once Complete has recorded the return value every later
                                // re-assert carries it; before that, keep whatever is there.
                                ReturnValue = returnValue ?? current.ReturnValue
                            }
                        };
                    })
                    .Subscribe(
                        _ => { },
                        ex => _diagnostics?.LogDebug(ex,
                            "ActivityLogLogger: publishing the log snapshot for {Path} failed",
                            activityLogPath),
                        // 🚨 #3117 — the highest-volume terminal _Activity writer in the platform
                        // (every kernel run, script, markdown execution and test run) bypasses
                        // ActivityLogAppender.Append, so it never fired the release that retires the
                        // activity's per-node hub. Its hubs waited out the 10-minute idle sweep
                        // instead. `status` is what THIS write asserted, so a Running snapshot is a
                        // no-op inside the seam.
                        //
                        // On COMPLETION, never on the emission: releasing tears the path's upstream
                        // sync streams down, and the write is still in flight when its value is
                        // emitted. Same rule, same reason, as Append's own Do arm.
                        () => ActivityLogAppender.ReleaseMirrorWhenFinal(
                            hub, activityLogPath, status, _diagnostics));
        }
        catch { /* never let logging break the script */ }
    }

    /// <summary>
    /// Moves everything above <see cref="ActivityLog.MessageWindowLimit"/> out of the published window
    /// and into an <see cref="ActivityLogSegment"/> satellite. MUST be called under
    /// <see cref="_publishLock"/>, which is also what makes the "one seal at a time" flag sound.
    ///
    /// <para>The counters advance in the write's SUCCESS callback, not here — so a failed segment write
    /// leaves the lines in the window and the next flush retries the same slice with a deterministic
    /// (re-used) segment index. Nothing is lost, nothing is duplicated, and no timer is involved.</para>
    /// </summary>
    private void SealOverflowLocked(ImmutableList<LogMessage> snapshot)
    {
        if (_sealInFlight) return;
        var unsealed = snapshot.Count - _sealedCount;
        if (unsealed <= ActivityLog.MessageWindowLimit) return;

        var take = unsealed - ActivityLog.MessageWindowKeep;
        var firstOrdinal = _sealedCount;
        var index = _sealedSegments;
        var chunk = snapshot.GetRange(firstOrdinal, take);

        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null) return;

        var segmentId = index.ToString("D6");
        var segmentNode = new MeshNode(segmentId, ActivityLogAppender.SegmentNamespace(activityLogPath))
        {
            Name = $"Log {firstOrdinal}-{firstOrdinal + take - 1}",
            NodeType = ActivityLogAppender.SegmentNodeType,
            // Satellites delegate access to a real main node; the activity's own path carries none.
            MainNode = SatelliteTableMapping.OwnerOfSatellitePath(activityLogPath),
            State = MeshNodeState.Active,
            Content = new ActivityLogSegment
            {
                Id = segmentId,
                FirstOrdinal = firstOrdinal,
                ActivityPath = activityLogPath,
                Messages = chunk,
            },
        };

        _sealInFlight = true;
        // CreateOrUpdateNode (not CreateNode): a retried seal re-writes the SAME index with the same
        // content rather than failing on an existing path.
        //
        // 🚨 #1790 — a SYNCHRONOUS using, never Observable.Using / RunAsSystem, for exactly the
        // reason spelled out on the publish write above: impersonation is an AsyncLocal scope, and
        // both reactive shapes open it on the SUBSCRIBING thread while disposing it on whichever
        // thread the write terminates. This call site is on the same thread as that one — the seal
        // runs inside PublishSnapshotLocked — so the latch it leaves behind runs the rest of the
        // script as System. #1785 fixed the publish and split the seal out to #1790 rather than
        // change it without a repro; ActivityLogSealIdentityTest is that repro.
        //
        // Sound here because the capture is synchronous: MeshService.CreateOrUpdateNode reads
        // AccessService.Context on the CALLING thread and pins it onto the request (RequestedBy)
        // and onto the delivery (PostOptions.WithAccessContext), so nothing re-reads the ambient
        // afterwards. The Subscribe sits inside the block as well, which costs nothing and keeps
        // the cold pipeline's synchronous prologue covered too.
        using (_accessService?.ImpersonateAsSystem())
            meshService.CreateOrUpdateNode(segmentNode)
                .Subscribe(
                    _ =>
                    {
                        lock (_publishLock)
                        {
                            _sealedCount = firstOrdinal + take;
                            _sealedSegments = index + 1;
                            _sealInFlight = false;
                        }
                    },
                    _ =>
                    {
                        // Best-effort, like every other write on this path: the lines stay in the window
                        // and the next flush retries them.
                        lock (_publishLock) _sealInFlight = false;
                    });
    }
}
