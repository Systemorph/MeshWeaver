using System.Collections.Concurrent;

namespace MeshWeaver.AI;

/// <summary>
/// Posted by ThreadExecution when execution completes, to notify the parent
/// thread's delegation tool that the child is done.
/// </summary>
public record DelegationCompletedEvent
{
    public required string ThreadPath { get; init; }
    public string? ResponseText { get; init; }
    public bool Success { get; init; }
}

/// <summary>
/// Static tracker for pending delegations. The delegation tool registers a callback,
/// ThreadExecution posts the event, the handler on the thread hub resolves it.
/// </summary>
public static class DelegationTracker
{
    private static readonly ConcurrentDictionary<string, Action<DelegationCompletedEvent>> Pending = new();

    public static void Register(string childThreadPath, Action<DelegationCompletedEvent> onComplete)
        => Pending[childThreadPath] = onComplete;

    public static bool TryComplete(DelegationCompletedEvent evt)
    {
        if (Pending.TryRemove(evt.ThreadPath, out var callback))
        {
            callback(evt);
            return true;
        }
        return false;
    }
}
