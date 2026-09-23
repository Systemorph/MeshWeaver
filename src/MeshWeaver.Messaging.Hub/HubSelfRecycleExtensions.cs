using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

/// <summary>
/// A hub asking to be recycled BY ITSELF: the one spelling, and the ordering it has to keep.
/// </summary>
public static class HubSelfRecycleExtensions
{
    /// <summary>
    /// Recycles <paramref name="hub"/> once the work it has ALREADY ACCEPTED has run: the
    /// <see cref="DisposeRequest"/> it posts to itself is decided as an ordinary turn, queued behind
    /// the hub's initialization gates, never ahead of them.
    ///
    /// <para>🚨 <b>Why a self-recycle cannot just post its <see cref="DisposeRequest"/>.</b>
    /// <see cref="DisposeRequest"/> is exempt from every initialization gate, by design: an
    /// EXTERNAL teardown (an operator recycle, a node delete, an owner's cascade) must reach a hub
    /// in any state, including one whose gates never open. A hub's own watcher, though, is armed in
    /// <c>WithInitialization</c> and can decide to recycle before the gates open: the overlay
    /// self-heal fires on the first usable replay of its NodeType stream when it had no type node
    /// in hand. Its <see cref="DisposeRequest"/> then overtook every request the activation had
    /// already parked behind <c>[DataContextInit, MeshNodeInit]</c>, and disposal discarded them
    /// (a <c>[DISPOSE-DISCARD]</c> Error per delivery, Systemorph/MeshWeaver#5356: a cache client's
    /// two <c>SubscribeRequest</c>s and its <c>UnsubscribeRequest</c> on <c>Deployments/build</c>).
    /// The hub was going down for a reason it had itself decided, while work it had accepted was
    /// still waiting in its own queue.</para>
    ///
    /// <para><b>The shape.</b> The decision is posted as an execution turn
    /// (an <see cref="ExecutionRequest"/>, the turn <see cref="IMessageHub.InvokeAsync(Action)"/> posts). That turn is not a lifecycle message, so the
    /// gates defer it FIFO with everything else. When the last gate opens, the backlog is restored in
    /// arrival order and the turn runs after the work that arrived before it. Only THEN is the
    /// <see cref="DisposeRequest"/> posted, behind everything the restore put ahead of it, and the
    /// ordinary quiesce drains the rest. On an already-initialized hub nothing is deferred, so this is
    /// the old self-post plus one turn. Nothing waits on a timer and no bound is involved: the gates
    /// are settled by their own time-boxes (opened, or failed so that the backlog is answered), so the
    /// turn is either run or answered.</para>
    ///
    /// <para>A teardown already under way when the turn runs needs no second one, and posting into it
    /// would only add a delivery the disposal has to account for, so the turn then does nothing.
    /// If the hub is disposed while the turn is still parked, the turn is discarded as the hub's
    /// OWN delivery (Debug, not an Error), because the hub went down anyway.</para>
    /// </summary>
    /// <param name="hub">The hub recycling itself.</param>
    /// <param name="reason">One sentence saying WHY, carried on the <see cref="DisposeRequest"/> and
    /// printed by <c>[QUIESCE-START]</c> and by every discard attribution (#3510, #3712).</param>
    /// <param name="logger">Where a fault of the recycle turn is reported; an execution turn's
    /// fault reaches nothing but its own callback, so without one it would be silent.</param>
    public static void RecycleSelfAfterAcceptedWork(
        this IMessageHub hub, string reason, ILogger? logger = null)
        // 🚨 Posted as the HUB, explicitly. The callers are watchers whose callbacks run on a
        // publisher's thread (a change feed's post-commit Do, a type stream's emission), which
        // carries no AccessContext. The DisposeRequest this used to post is [CanBeIgnored] and
        // needs none, but the execution turn is an ordinary post and the pipeline fails a
        // context-free one closed. The hub recycling itself is infrastructure, so it posts as itself.
        => hub.Post(
            new ExecutionRequest(
                _ =>
                {
                    if (!hub.IsDisposing)
                        hub.Post(new DisposeRequest { Reason = reason },
                            o => o.WithTarget(hub.Address));
                    return Task.CompletedTask;
                },
                ex =>
                {
                    logger?.LogWarning(ex,
                        "Self-recycle of {Address} could not be posted ({Reason}); the hub keeps "
                        + "serving until it is recycled some other way", hub.Address, reason);
                    return Task.CompletedTask;
                }),
            o => o.WithTarget(hub.Address).ImpersonateAsHub(hub.Address));
}
