using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// 🚨 The CONSUMER-facing half of stream liveness — the thing whose absence cost a production NRE
/// (Systemorph/MeshWeaver#3321, step 1 of 3).
///
/// <para><b>The defect this closes.</b> <see cref="ISynchronizationStream.Hub"/> is declared
/// NON-NULLABLE and 47 sites dereference it. A stream whose owner has been torn down is a corpse
/// that still answers that property, and <see cref="ISynchronizationStream"/> exposed no member
/// with which a consumer could ask. A predecessor of <c>SynchronizationStream</c> tried to make the
/// corpse honest by building a DEAD stream with <c>Hub = null!</c> and documenting that "every code
/// path that touches Hub goes through <c>TryGetActiveHub</c>". No consumer honoured that — it could
/// not, the method was private to one file — and the result was an NRE inside
/// <c>LayoutAreaHost</c>'s constructor (<c>Stream.Hub.ServiceProvider…</c>) during a recycle window,
/// which escaped to the subscriber as a TERMINAL <c>DeliveryFailure</c>: a page that subscribed
/// mid-recycle was told "this failed forever" instead of "ask again".</para>
///
/// <para><b>Why an extension and not a member on the interface.</b>
/// <see cref="StreamLiveness"/> records both reasons and neither has expired. Adding a member to a
/// public interface breaks every downstream implementer, and these assemblies ship as packages. And
/// <c>IStreamLivenessSource.Source</c> is not a consumer-facing concept — it exists so the reduce
/// chain is walked in exactly ONE place, and a consumer walking it by hand is the ad-hoc predicate
/// that whole design removed. So the PREDICATE becomes reachable while the interface it reads stays
/// internal: consumers gain the ability to ask, implementers are untouched.</para>
///
/// <para><b>One definition, not a second one.</b> Both methods delegate to
/// <see cref="StreamLiveness.IsUsable"/> and add no logic of their own. That is deliberate:
/// <see cref="StreamLiveness"/> exists because three hand-copied liveness predicates diverged
/// (#1455), and a public wrapper that re-implemented the check would be the fourth. The name is
/// kept as <c>IsUsable</c> for the same reason — a dozen comments across <c>Workspace</c>,
/// <c>JsonSynchronizationStream</c> and <c>StreamNotConvergingException</c> already point readers at
/// "StreamLiveness.IsUsable", and a consumer following one of them must find the thing it names.</para>
///
/// <para>🚨 <b>All three steps of #3321 have landed, in the only order that works.</b> Step 1
/// (#3380) added these two methods and changed no behaviour. Step 2 (#3386) migrated the consumer
/// dereference sites onto <see cref="TryGetHub"/>. Step 3 then released the reference — from BOTH
/// ends, because only 11 of the 1 496 corpses in the production dump sat under a stream that had
/// itself been disposed; the other 1 485 were hosted sub-hubs killed by their parent's teardown
/// under a stream nobody ever disposed. Dropping the reference before the call sites could answer
/// "no" is precisely the production incident above, in the same order it happened — which is why
/// this file came first. See <c>Doc/Architecture/StreamLivenessAndTheHubReference</c>.</para>
/// </summary>
public static class SynchronizationStreamLiveness
{
    /// <summary>
    /// True when <paramref name="stream"/> is still safe to use: it and every ancestor in its
    /// reduce chain are undisposed, unfaulted, and owned by a hub that has not begun winding down.
    ///
    /// <para>The ancestor walk is not optional pedantry. A reduced stream is its parent's SIBLING —
    /// <c>WorkspaceStreams.CreateReducedStream</c> hosts its <c>sync/{id}</c> sub-hub under the
    /// parent's <c>Host</c> and merely registers it for disposal ON the parent — so a child stays
    /// alive for the whole teardown cascade and will happily mirror a source that is already
    /// dead.</para>
    ///
    /// <para>Answers <c>false</c> for <c>null</c>, so a nullable stream can be tested directly.</para>
    /// </summary>
    /// <param name="stream">The stream to judge, or <c>null</c>.</param>
    /// <returns><c>false</c> for <c>null</c>, for a disposed or terminally faulted stream, and for
    /// a live stream whose source chain contains one.</returns>
    public static bool IsUsable(this ISynchronizationStream? stream)
        => StreamLiveness.IsUsable(stream);

    /// <summary>
    /// The stream's hub if the stream is still usable, otherwise <c>null</c> — the accessor that
    /// can answer "no", and the migration target for a <c>stream.Hub.Something</c> dereference.
    ///
    /// <para>This is the public counterpart of <c>SynchronizationStream</c>'s private
    /// <c>TryGetActiveHub</c>. That one guards the stream's OWN write paths and always did its job;
    /// what was missing was the same question being answerable from outside the file, which is why
    /// the in-file discipline did not save the predecessor.</para>
    ///
    /// <para>Never throws and never returns a hub belonging to a dead chain, so
    /// <c>stream.TryGetHub() is { } hub</c> is a complete guard: <see cref="IsUsable"/> already
    /// treats an absent hub as dead, so there is no path here that dereferences a null.</para>
    /// </summary>
    /// <param name="stream">The stream to read the hub from, or <c>null</c>.</param>
    /// <returns>The hub, or <c>null</c> when the stream is <c>null</c>, disposed, faulted, or
    /// mirrors something that is.</returns>
    public static IMessageHub? TryGetHub(this ISynchronizationStream? stream)
        => stream.IsUsable() ? stream!.Hub : null;

    /// <summary>
    /// The stream's hub if it still HOLDS one, otherwise <c>null</c> — the PRESENCE accessor, and
    /// the migration target for an OWNER-SIDE <c>stream.Hub.Something</c> whose previous behaviour
    /// was unconditional (Systemorph/MeshWeaver#3321, step 3 of 3).
    ///
    /// <para>🚨 <b>Presence is a different question from liveness, and using the wrong one is a
    /// behaviour change — measured, not theorised.</b> Migrating
    /// <c>DataSource.Initialized</c> (<c>Task.WhenAll(streams.Select(s =&gt; s.Hub.Started))</c>) onto
    /// <see cref="TryGetHub"/> silently BROKE
    /// <c>DataContextFaultedInitBeforeStreamHubBoundTest</c>: a stream whose initial load faults is
    /// not <see cref="IsUsable"/> — that is the whole point of <c>IsFaulted</c> — so the guard
    /// excluded it from the WhenAll, <c>Initialized</c> completed SUCCESSFULLY, and a hub whose
    /// data source had thrown started answering requests as though nothing had happened. Faulting
    /// that <c>Started</c> task is exactly what must still be awaited.</para>
    ///
    /// <para>So the rule is: <b><see cref="TryGetHub"/> where the site is asking "should I use this
    /// stream?" (a consumer, a cache, a read); this one where the site is asking "is the reference
    /// still there?" (an owner writing into its own stream, where refusing a merely winding-down
    /// hub would change what the code does).</b> Step 2 declined to tighten already-working sites
    /// for the same reason, and this method is what lets step 3 make them null-safe without
    /// tightening them either.</para>
    ///
    /// <para>Answers <c>null</c> for a <c>null</c> stream, so a nullable stream can be tested
    /// directly. It reads the field ONCE — which is the other half of the point: a caller that
    /// re-reads <c>stream.Hub</c> after its own guard is check-then-act across threads, because a
    /// <c>Dispose()</c> elsewhere can land in between.</para>
    /// </summary>
    /// <param name="stream">The stream to read the hub from, or <c>null</c>.</param>
    /// <returns>The hub, or <c>null</c> when the stream is <c>null</c> or has released it.</returns>
    public static IMessageHub? HubIfHeld(this ISynchronizationStream? stream)
        => stream?.Hub;

    /// <summary>
    /// The stream's hub, or a TRANSIENT <see cref="HubDisposingException"/> when the stream has
    /// RELEASED it — the same PRESENCE question as <see cref="HubIfHeld"/>, for a surface whose
    /// signature admits no absent value (Systemorph/MeshWeaver#3321, step 3 of 3).
    ///
    /// <para>🚨 <b>This is NOT a liveness predicate and must never grow into one.</b>
    /// <see cref="IsUsable"/> is the ONE liveness answer — it walks the reduce chain, counts a
    /// faulted store, and refuses a hub that has merely begun winding down. This method asks a
    /// strictly narrower and purely factual question: <b>is the reference still there?</b> Step 3
    /// made <c>Hub</c> clearable — <c>SynchronizationStream.Dispose()</c> and the stream's
    /// hub-death hook both drop it, which is what releases the leaked
    /// <c>stream → dead hub → resolved state</c> graph — so "absent" became a state the field can
    /// really be in. Tightening this to <see cref="IsUsable"/> would refuse a WINDING-DOWN or
    /// FAULTED but still-present hub and so change behaviour on live paths (see
    /// <see cref="HubIfHeld"/> for the measured instance); keeping them separate is also what stops
    /// this from becoming the fourth hand-copied liveness predicate (#1455).</para>
    ///
    /// <para><b>Why a throw is the right refusal here.</b> The callers are patch reducers
    /// (<c>StandardReducers</c>, <c>MeshDataSource.PatchMeshNode</c>) whose signature is
    /// <c>… → ChangeItem&lt;T&gt;</c>: they MUST return a change, so a throw is the only channel
    /// they have, and inventing a sentinel change would be worse than saying so.
    /// <see cref="HubDisposingException"/> is an <see cref="System.ObjectDisposedException"/>, so
    /// it classifies as <c>ErrorType.ShuttingDown</c> — the transient "ask again" answer — instead
    /// of the terminal <c>DeliveryFailure</c> the predecessor's raw NRE produced. Their single
    /// gateway, <c>JsonSynchronizationStream.ToChangeItem</c>, catches exactly this type and
    /// answers with the <c>null</c> its own signature already models, so a reducer racing a
    /// disposal degrades to "no patch derived" rather than to a fault.</para>
    /// </summary>
    /// <param name="stream">The stream to read the hub from.</param>
    /// <returns>The stream's hub; never <c>null</c>.</returns>
    /// <exception cref="HubDisposingException">The stream has released its hub.</exception>
    public static IMessageHub RequireHub(this ISynchronizationStream stream)
        => stream.HubIfHeld()
           ?? throw new HubDisposingException(stream.Host.Address, stream.Reference);
}
