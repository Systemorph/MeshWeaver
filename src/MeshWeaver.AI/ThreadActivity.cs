using System.Text.Json;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.AI;

/// <summary>
/// The live activity a thread LIST shows per row (the Claude-Code-style session indicator):
/// a round is running, input is queued behind a running round, or the thread is at rest
/// waiting for the user.
/// </summary>
public enum ThreadActivityKind
{
    /// <summary>A round is in flight (<see cref="ThreadExecutionStatus.StartingExecution"/> /
    /// <see cref="ThreadExecutionStatus.Executing"/>) — the agent is evaluating.</summary>
    Evaluating,

    /// <summary>No round in flight but <see cref="MeshThread.PendingUserMessages"/> holds
    /// submitted-not-yet-ingested input — the submission watcher will dispatch it.</summary>
    Queued,

    /// <summary>At rest — the agent finished (or never started); it is the user's turn.</summary>
    Awaiting
}

/// <summary>
/// Pure derivation of a thread's <see cref="ThreadActivityKind"/> + queued-message count for list
/// indicators. Two overloads: the typed one, and a CHEAP raw-content probe for hot paths that see
/// one whole-snapshot emission per streamed token (deserializing every thread's full content there
/// would burn the circuit) — the probe reads only <c>status</c> and <c>pendingUserMessages</c>,
/// with the property names derived from the serializer's naming policy, never hard-coded literals.
/// </summary>
public static class ThreadActivity
{
    /// <summary>Derives the activity of <paramref name="thread"/> (null ⇒ awaiting, 0 queued).</summary>
    public static (ThreadActivityKind Kind, int QueuedCount) Of(MeshThread? thread)
    {
        var queued = thread?.PendingUserMessages.Count ?? 0;
        if (thread?.IsExecuting == true)
            return (ThreadActivityKind.Evaluating, queued);
        return queued > 0 ? (ThreadActivityKind.Queued, queued) : (ThreadActivityKind.Awaiting, 0);
    }

    /// <summary>
    /// Cheap probe over a node's raw content — a typed <see cref="MeshThread"/> takes the typed
    /// path; a <see cref="JsonElement"/> (the cache / cross-hub representation) is probed for the
    /// two fields only. Anything else reads as awaiting.
    /// </summary>
    public static (ThreadActivityKind Kind, int QueuedCount) Of(object? content, JsonSerializerOptions options)
    {
        switch (content)
        {
            case MeshThread typed:
                return Of(typed);
            case JsonElement { ValueKind: JsonValueKind.Object } je:
            {
                var name = (string s) => options.PropertyNamingPolicy?.ConvertName(s) ?? s;
                var queued = je.TryGetProperty(name(nameof(MeshThread.PendingUserMessages)), out var pending)
                             && pending.ValueKind == JsonValueKind.Object
                    ? CountProperties(pending)
                    : 0;
                var status = ReadStatus(je, name(nameof(MeshThread.Status)));
                if (status is ThreadExecutionStatus.StartingExecution or ThreadExecutionStatus.Executing)
                    return (ThreadActivityKind.Evaluating, queued);
                return queued > 0 ? (ThreadActivityKind.Queued, queued) : (ThreadActivityKind.Awaiting, 0);
            }
            default:
                return (ThreadActivityKind.Awaiting, 0);
        }
    }

    private static int CountProperties(JsonElement obj)
    {
        var count = 0;
        foreach (var _ in obj.EnumerateObject())
            count++;
        return count;
    }

    private static ThreadExecutionStatus ReadStatus(JsonElement je, string propertyName)
    {
        if (!je.TryGetProperty(propertyName, out var status))
            return ThreadExecutionStatus.Idle;                       // default-suppressed ⇒ Idle
        return status.ValueKind switch
        {
            JsonValueKind.String when Enum.TryParse<ThreadExecutionStatus>(
                status.GetString(), ignoreCase: true, out var parsed) => parsed,
            JsonValueKind.Number when status.TryGetInt32(out var n)
                && Enum.IsDefined(typeof(ThreadExecutionStatus), n) => (ThreadExecutionStatus)n,
            _ => ThreadExecutionStatus.Idle
        };
    }
}
