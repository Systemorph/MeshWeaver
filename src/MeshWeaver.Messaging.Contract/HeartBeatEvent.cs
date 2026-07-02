namespace MeshWeaver.Messaging;

/// <summary>
/// Published by executing hubs (e.g., _Exec during AI streaming) to their parent hub
/// to signal that the grain should stay alive during long-running operations.
/// Handled by every mesh node hub — calls GrainKeepAliveCallback if registered.
/// <para>Marked <see cref="CanBeIgnoredAttribute"/> so hubs without a handler
/// (test clients, host hubs, monolith infrastructure) don't generate a
/// DeliveryFailure response — the heartbeat is fire-and-forget liveness
/// signal; the receiver's lack of handler is fine.</para>
/// </summary>
[SystemMessage]
[CanBeIgnored]
public record HeartBeatEvent;

/// <summary>
/// Registered on the hub configuration by the Orleans grain during activation.
/// Provides a bridge from the hub's HeartBeatEvent handler to the grain's DelayDeactivation.
/// In monolith mode, no callback is set — HeartBeatEvent is a no-op.
/// </summary>
public record GrainKeepAliveCallback(Action KeepAlive);

/// <summary>
/// Callback registered by Orleans grain to support long-running operations.
/// The hub calls BeginOperation before starting an async operation (AI streaming, etc.).
/// The returned IDisposable stops the keep-alive when disposed.
/// In monolith mode, no callback is set — returns a no-op disposable.
/// </summary>
public record GrainLongRunningOperationCallback(Func<IDisposable> BeginOperation);

/// <summary>
/// Registered on the hub configuration by the Orleans grain during activation
/// (alongside <see cref="GrainKeepAliveCallback"/>). Requests IMMEDIATE grain
/// deactivation (<c>Grain.DeactivateOnIdle</c>) as an OUT-OF-BAND escape hatch that
/// does NOT ride the hub's message queue.
/// <para>WHY: the hosted hub's action block runs on the grain's
/// ActivationTaskScheduler. When a round wedges that scheduler (issue #147: an LLM
/// streaming continuation that never resumed occupied the single scheduler slot,
/// queueing 1376 messages), every rescue that is itself a hub message — including the
/// stuck-round watchdog's force-Idle <c>stream.Update</c> — joins the blocked backlog
/// and can never be processed. Invoking this callback deactivates the grain WITHOUT
/// going through the blocked action block; deactivation disposes the hub, which fires
/// the round's <c>executionCts.Cancel()</c> via <c>RegisterForDisposal</c>, tearing
/// down the stuck call. The next access re-activates the grain fresh.</para>
/// <para>In monolith mode, no callback is set — there is no grain scheduler to wedge,
/// so callers treat the absence as a no-op (see
/// <c>MessageHubExtensions.RequestGrainDeactivation</c>).</para>
/// </summary>
public record GrainDeactivateCallback(Action Invoke);
