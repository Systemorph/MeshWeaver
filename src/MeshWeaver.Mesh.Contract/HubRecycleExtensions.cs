using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Recycling a node's hub — the ONE surface, and the one rule that makes it work: <b>the caller
/// must not be the thing it is recycling.</b>
///
/// <para>🚨 <b>Why this is an extension on the CALLER'S hub and not an area on the target</b>
/// (#2202). Recycle used to be a confirmation layout area hosted on the very hub it tears down.
/// Its confirm button pushed a <c>RedirectControl</c> into the area stream and then posted
/// <see cref="DisposeRequest"/> to that same hub — so the redirect had to survive a teardown of the
/// stream carrying it. In-process queue order is not wire order: the dispose reached the hub before
/// the area update flushed to the client, the hub recycled correctly, and the user saw a dead
/// button. Re-ordering the two posts (2026-07) did not help, because the race is structural: a
/// dying hub cannot be relied upon to deliver its own last frame. Every caller of this method
/// therefore holds a hub that OUTLIVES the target — the portal circuit's hub, a session hub, an
/// MCP hub, a test client — and the whole flow (confirm, dispose, wait, redirect) runs there.</para>
///
/// <para><b>Why the wait is a READ and not a poll.</b> Once the <see cref="DisposeRequest"/> is
/// posted, "has the address come back?" is answered by simply reading the node:
/// <c>GetMeshNode</c> already treats an <see cref="ErrorType.ShuttingDown"/> NACK as
/// "recycling, NOT absent" and re-probes on its own paced loop inside the caller's budget,
/// delivering the node the moment the address reactivates (#1726). So there is no timer here, no
/// watchdog, and no sleep — the framework's read IS the wait, and a recycle that outlasts the whole
/// budget surfaces the typed <c>AddressRecyclingException</c> rather than a silent nothing.</para>
/// </summary>
public static class HubRecycleExtensions
{
    /// <summary>
    /// How long a caller waits for a recycled address to answer again before giving up. Sized for a
    /// PACKAGE-ROOT hub, whose teardown drains a subtree of hosted children and can legitimately
    /// occupy several seconds — not for the sub-second recycle of a leaf node. It bounds the
    /// caller's patience, never the teardown: the hub's own disposal watchdog owns that.
    /// </summary>
    public static readonly TimeSpan DefaultRecycleBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Recycles the hub at <paramref name="path"/> and emits the node once that address answers
    /// again — i.e. once a FRESH activation has served a read.
    ///
    /// <para>Cold: nothing happens until you subscribe, and the <see cref="DisposeRequest"/> is
    /// posted on subscribe (never at call time), so a composed-but-unsubscribed chain cannot
    /// silently tear a hub down.</para>
    ///
    /// <para>🚨 <paramref name="hub"/> MUST NOT be the hub at <paramref name="path"/> — see the
    /// remarks on this class. Callers pass the surviving hub they already hold.</para>
    /// </summary>
    /// <param name="hub">The caller's hub — one that outlives the target.</param>
    /// <param name="path">Path of the node whose hub is recycled.</param>
    /// <param name="budget">How long to wait for the address to answer again;
    /// <see cref="DefaultRecycleBudget"/> when omitted.</param>
    /// <returns>The node as served by the re-activated address. Errors with
    /// <c>AddressRecyclingException</c> if the address is still recycling when the budget runs out.</returns>
    public static IObservable<MeshNode?> RecycleNode(
        this IMessageHub hub, string path, TimeSpan? budget = null)
        => Observable.Defer(() =>
        {
            hub.Post(new DisposeRequest(), o => o.WithTarget(new Address(path)));
            // The read is issued AFTER the dispose is posted, so it queues behind it at the target
            // and is answered by the reactivated hub (or NACKed ShuttingDown and re-probed until it
            // is). Deliberately NOT ReadTimeoutBehavior.EmitNull: "I could not tell" must reach the
            // caller as an error, because the caller's next act is to send a user somewhere.
            return hub.GetMeshNode(path, budget ?? DefaultRecycleBudget);
        });
}
