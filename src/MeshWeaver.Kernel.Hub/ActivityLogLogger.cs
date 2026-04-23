using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
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
    private ImmutableList<LogMessage> _messages = ImmutableList<LogMessage>.Empty;
    private int _completed;

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

        // Best-effort push. Failures never surface into the script — the activity
        // log is an observability surface, not a correctness path.
        PublishSnapshot(ActivityStatus.Running, finish: false);
    }

    /// <summary>
    /// Finalise the activity log with <paramref name="status"/> and flush a last
    /// update so subscribers see the terminal state.
    /// </summary>
    public void Complete(ActivityStatus status)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        PublishSnapshot(status, finish: true);
    }

    private void PublishSnapshot(ActivityStatus status, bool finish)
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
                End = finish ? DateTime.UtcNow : null
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
            hub.Post(
                DataChangeRequest.Update([node]),
                o => o.WithTarget(new Address(activityLogPath)));
        }
        catch { /* never let logging break the script */ }
    }
}
