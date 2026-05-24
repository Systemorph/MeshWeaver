namespace MeshWeaver.AI.Delegation;

/// <summary>
/// A lifecycle event for a single in-flight delegation. Emitted on
/// <see cref="IAgentChat.Delegations"/> by <c>ExecuteDelegationAsync</c>:
/// <list type="bullet">
///   <item><see cref="DelegationLifecycle.Dispatched"/> — sub-thread node
///         created, parent's tool call has the path stamped on it.</item>
///   <item><see cref="DelegationLifecycle.Active"/> — sub-thread has
///         reported <c>IsExecuting=true</c> at least once.</item>
///   <item><see cref="DelegationLifecycle.Terminal"/> — sub-thread settled
///         (completed, cancelled, errored, or heartbeat-detected dead).</item>
/// </list>
///
/// <para>Single source of truth for "which sub-threads are this chat session
/// actively waiting on?" The cancel watcher and tool-call-stamper subscribe
/// to this stream; no separate registry / dictionary. Replaces the legacy
/// <c>chat.DelegationPaths</c> dictionary keyed by display-name.</para>
/// </summary>
public sealed record DelegationEvent(
    string CallId,
    string SubThreadPath,
    DelegationLifecycle Phase);

/// <summary>
/// Lifecycle phase of a single delegation. Monotonic:
/// Dispatched → Active → Terminal. Phases skip is allowed (e.g.
/// Dispatched → Terminal if sub-thread creation fails before reaching
/// Active).
/// </summary>
public enum DelegationLifecycle
{
    /// <summary>Sub-thread node + cells created; tool call stamped with path.</summary>
    Dispatched,

    /// <summary>Sub-thread has reported <c>IsExecuting=true</c> at least once.</summary>
    Active,

    /// <summary>Sub-thread settled (completed / cancelled / errored / heartbeat-killed).</summary>
    Terminal,
}
