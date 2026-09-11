using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
///
/// <para>🚨 <b>And why it may WAIT before it posts</b> (#3510). A recycle aimed at a package root
/// that an install is writing under strands that install's work: the root's per-node children go
/// down with it, the writes they owed acks for are never answered, and the handler that owed its
/// reply to one of those acks never replies. So the post is deferred while
/// <see cref="PackageRootInstallLeases"/> says an install holds that exact path, and runs the
/// moment the install releases it. Nothing is dropped, nothing is retried, no bound moves — the
/// release is a state that always arrives, because the install's lease is tied to its own
/// subscription. With no install holding the root this is a straight pass-through, which is the
/// common case and is a positive control in its own right.</para>
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
    /// <param name="reason">One sentence saying WHY, carried on the <see cref="DisposeRequest"/>
    /// and printed by the target's <c>[QUIESCE-START]</c> (Systemorph/MeshWeaver#3510). Omitted, it
    /// falls back to naming this API and the asking hub — which is still an answer, unlike the
    /// blank it replaces.</param>
    /// <returns>The node as served by the re-activated address. Errors with
    /// <c>AddressRecyclingException</c> if the address is still recycling when the budget runs out.</returns>
    public static IObservable<MeshNode?> RecycleNode(
        this IMessageHub hub, string path, TimeSpan? budget = null, string? reason = null)
        => WaitWhileAnInstallHoldsIt(hub, path)
            .SelectMany(_ => Observable.Defer(() =>
            {
                hub.Post(
                    new DisposeRequest
                    {
                        Reason = reason
                                 ?? $"HubRecycleExtensions.RecycleNode: {hub.Address} asked for this "
                                    + "address to be recycled and is waiting for a fresh activation to "
                                    + "answer",
                    },
                    o => o.WithTarget(new Address(path)));
                // The read is issued AFTER the dispose is posted, so it queues behind it at the target
                // and is answered by the reactivated hub (or NACKed ShuttingDown and re-probed until it
                // is). Deliberately NOT ReadTimeoutBehavior.EmitNull: "I could not tell" must reach the
                // caller as an error, because the caller's next act is to send a user somewhere.
                return hub.GetMeshNode(path, budget ?? DefaultRecycleBudget);
            }));

    /// <summary>
    /// Emits once nothing is installing under <paramref name="path"/> — at once in the ordinary
    /// case, and on the install's release when one holds it (#3510).
    ///
    /// <para>🚨 <b>The deferral is announced in both directions.</b> A recycle that silently waited
    /// would be indistinguishable from a recycle that was never asked for, which is exactly the
    /// unreadability that cost this issue six occurrences; and a lease that somehow outlived its
    /// install would show up as nothing at all. So the wait logs when it starts, naming the holder,
    /// and again when it proceeds. <b>Information</b>, not Debug: it is one line per deferred
    /// recycle — an event that by construction only happens while a package install is running —
    /// and the reader needing it is looking at a stalled install, not at a trace.</para>
    ///
    /// <para>No registry (a host composed without <c>MeshBuilder</c>'s registrations, a bare test
    /// hub) means no lease can exist, so the answer is "proceed" and the pre-#3510 behaviour is
    /// unchanged.</para>
    /// </summary>
    private static IObservable<System.Reactive.Unit> WaitWhileAnInstallHoldsIt(
        IMessageHub hub, string path)
        => Observable.Defer(() =>
        {
            var leases = hub.ServiceProvider.GetService<PackageRootInstallLeases>();
            if (leases?.HeldBy(path) is not { } holder)
                return Observable.Return(System.Reactive.Unit.Default);

            var logger = hub.ServiceProvider
                .GetService<ILoggerFactory>()?.CreateLogger(typeof(HubRecycleExtensions).FullName!);
            logger?.LogInformation(
                "[Recycle] deferring the recycle of {Path} requested by {Asker}: an install is "
                + "writing under that root ({Holder}). Tearing it down now would strand that "
                + "install's writes — their owners would go down owing acks nobody would ever "
                + "send. The recycle runs when the install releases the root; nothing is retried "
                + "and no bound is widened",
                path, hub.Address, holder);
            return leases.WhenReleased(path)
                .Do(_ => logger?.LogInformation(
                    "[Recycle] the install released {Path} — proceeding with the recycle requested "
                    + "by {Asker}", path, hub.Address));
        });
}
