using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Delegation;

/// <summary>
/// Posted by the parent thread hub to itself on a periodic timer
/// (<c>Observable.Interval(1s)</c> registered in <c>WithInitialization</c>).
/// The handler walks every active delegation sub-thread, reads its current
/// <see cref="Thread"/> via the mesh-node cache, and applies the heartbeat
/// predicate: <c>IsExecuting=true AND (now - LastActivityAt) &gt; HeartbeatTimeout
/// AND (now - ExecutionStartedAt) &gt; ColdStartGrace</c>. On match it posts
/// <see cref="CancelDelegationSubThread"/>.
/// </summary>
[SystemMessage]
public sealed record HeartbeatTick;

/// <summary>
/// Posted to the parent thread hub by the heartbeat handler when a sub-thread
/// has gone unresponsive. The handler issues a single
/// <c>nodeCache.Update(SubThreadPath, ... RequestedCancellationAt = now)</c> —
/// the SAME primitive the GUI Stop button uses — which propagates through the
/// sub-thread's own cancel watcher and tears down its CTS.
/// </summary>
[SystemMessage]
public sealed record CancelDelegationSubThread(string SubThreadPath, string Reason);
