using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// <see cref="ILogger"/> implementation that appends each log call to the
/// <c>Messages</c> list of a target <c>ActivityLog</c> MeshNode. The node's
/// workspace is the Code hub's owning hub; updates are posted as
/// <see cref="DataChangeRequest"/> so the hub's workspace stream ticks and
/// subscribers (<c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c>)
/// receive live message updates.
///
/// Injected into the script's <c>Log</c> global per <see cref="SubmitCodeRequest"/>
/// so every concurrent run writes to its own ActivityLog.
/// </summary>
internal sealed class ActivityLogLogger(IMessageHub hub, string activityLogPath) : ILogger
{
    private readonly object _lock = new();
    // 🚨 Kernel activity-log publishing is INFRASTRUCTURE observability and fires from a
    // throttle TIMER thread (Observable.Timer below) that never inherited the script
    // runner's AccessContext → the DataChangeRequest post would be context-null and
    // RLS-denied on the activity's partition → the activity log never ticks. Publish
    // under System (Permission.All) — same rule as compile (#2) / user-activity (#3).
    private readonly AccessService? _accessService = hub.ServiceProvider.GetService<AccessService>();
    private ImmutableList<LogMessage> _messages = ImmutableList<LogMessage>.Empty;
    private int _completed;

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
                Observable.Timer(TimeSpan.FromMilliseconds(ThrottleMs))
                    .Subscribe(_ =>
                    {
                        Interlocked.Exchange(ref _publishScheduled, 0);
                        if (Volatile.Read(ref _completed) == 0)
                        {
                            Interlocked.Exchange(ref _lastPublishTicks, Environment.TickCount64);
                            PublishSnapshot(ActivityStatus.Running, finish: false);
                        }
                    });
            }
            return;
        }

        Interlocked.Exchange(ref _lastPublishTicks, now);
        PublishSnapshot(ActivityStatus.Running, finish: false);
    }

    /// <summary>
    /// Finalise the activity log with <paramref name="status"/> and flush a last
    /// update so subscribers see the terminal state. Optionally records the
    /// script's <paramref name="returnValue"/> on the activity content so request
    /// handlers that triggered the script (e.g. <c>ExportDocumentHandler</c>) can
    /// deserialize it on terminal status without a side-channel MeshNode.
    /// </summary>
    public void Complete(ActivityStatus status, JsonElement? returnValue = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        PublishSnapshot(status, finish: true, returnValue);
    }

    private void PublishSnapshot(ActivityStatus status, bool finish, JsonElement? returnValue = null)
    {
        ImmutableList<LogMessage> snapshot;
        lock (_lock) { snapshot = _messages; }

        try
        {
            // We don't round-trip the MeshNode through a query — we just post a
            // fresh ActivityLog content payload. The node hub's DataChangeRequest
            // handler merges it (the Content field is a POCO, so Updates([node])
            // replaces Content wholesale — fine for append-only Messages).
            var log = new ActivityLog("ScriptExecution")
            {
                Messages = snapshot,
                Status = status,
                End = finish ? DateTime.UtcNow : null,
                ReturnValue = returnValue
            };

            var segments = activityLogPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 1) return;
            var id = segments[^1];
            var ns = segments.Length > 1 ? string.Join('/', segments[..^1]) : "";

            // Dispatch a DataChangeRequest targeting the activity log's address.
            // The target hub's handler applies the update to its workspace stream,
            // which ticks any MeshNodeReference subscribers for live visibility.
            var node = new MeshNode(id, ns)
            {
                Name = $"Activity {id[..Math.Min(8, id.Length)]}",
                NodeType = "Activity",
                State = MeshNodeState.Active,
                Content = log
            };
            // Publish as System: this fires from the throttle timer thread (no inherited
            // identity); the activity log is infrastructure observability, not a user write.
            // ImpersonateAsSystem sets System unconditionally and the post reads the
            // AccessContext synchronously at Post time, so the scope covers the stamp.
            using (_accessService?.ImpersonateAsSystem())
                hub.Post(
                    DataChangeRequest.Update([node]),
                    o => o.WithTarget(new Address(activityLogPath)));
        }
        catch { /* never let logging break the script */ }
    }
}
