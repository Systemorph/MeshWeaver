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
