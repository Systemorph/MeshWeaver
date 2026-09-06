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
/// <para>🚨 <b>This is step 1 alone, and it changes no behaviour.</b> Steps 2 and 3 of #3321 —
/// migrating the 47 dereference sites onto <see cref="TryGetHub"/>, and only THEN dropping the
/// <c>Hub</c> reference on disposal so the leaked hub graph is released — are deliberately NOT done
/// here. Dropping the reference before the call sites can answer "no" is precisely the production
/// incident above, in the same order it happened.</para>
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
}
