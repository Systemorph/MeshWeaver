using System.Collections.Concurrent;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Data;

/// <summary>
/// Which of the ways a <c>sync/{id}</c> sub-hub can be absent actually applies to the stream a
/// refused message named — see <see cref="SyncStreamActivationLedger"/> for why they had to be
/// told apart.
/// </summary>
internal enum LostStreamCause
{
    /// <summary>
    /// This hub activation never registered a <c>sync/{id}</c> for that stream id. The subscriber
    /// is talking to an activation that no longer exists (a per-node hub deactivated and came back
    /// under a live subscription), or its subscribe never completed here.
    /// </summary>
    NeverRegistered,

    /// <summary>
    /// This hub served the stream and was then told to stop: an <see cref="UnsubscribeRequest"/>
    /// for it reached this hub. The action raced a teardown the subscriber itself asked for — the
    /// designed refusal.
    /// </summary>
    ReleasedBySubscriber,

    /// <summary>
    /// This hub served the stream on the current activation and was never told to stop, so the
    /// OWNER side ended it — an idle release, a workspace eviction, a sub-hub teardown — while the
    /// subscriber was still attached.
    /// </summary>
    ReapedByOwner,

    /// <summary>
    /// 🚨 Not a cause: the honest "I no longer hold the record". The ledger is bounded, and this
    /// stream id is neither in it nor provably absent from it because entries have already aged
    /// out. It exists so a FULL ledger can never masquerade as
    /// <see cref="NeverRegistered"/> — the one reading that would send a reader hunting a
    /// reactivation that never happened.
    /// </summary>
    NoRecord,
}

/// <summary>
/// 🚨 What splits the <c>REFUSING …</c> fingerprint into the three things it was conflating
/// (issue #3986 residue).
///
/// <para>The refusal sentence used to name all three of "disposed circuit, released read stream, or
/// never-created sync hub" in ONE line, so the incident fingerprint could not tell a DESIGNED
/// refusal (the subscriber released the stream and a click raced the release it asked for) from a
/// LIVE defect (an owner-side per-node hub deactivated under a subscriber that is still there). The
/// issue was filed on the first, fixed, deployed — and then reopened by an occurrence of the third,
/// because one sentence answered for both. Two triages spent their time re-deriving which one they
/// were looking at.</para>
///
/// <para><b>What it records, and nothing more.</b> Per stream id, on THIS hub activation: whether a
/// <c>sync/{id}</c> sub-hub was registered here (<see cref="RecordSyncHubRegistered"/>, from the
/// <c>SynchronizationStream</c> constructor — the one place that creates one), and whether an
/// <see cref="UnsubscribeRequest"/> for it reached this hub (<see cref="RecordUnsubscribeReceived"/>,
/// from <c>RouteStreamMessage</c>). Two booleans. It changes no behaviour, holds no message, and is
/// read only when a message is already being refused: nothing retries, nothing waits, no bound
/// moves.</para>
///
/// <para><b>An INSTANCE owned by the hub</b> — registered per hub scope beside
/// <see cref="IWorkspace"/> — never static. Its lifetime IS the activation it answers about, which
/// is what makes "on this activation" mean something: a recycled owner starts from empty, and every
/// stream a live subscriber still holds correctly reads as <see cref="LostStreamCause.NeverRegistered"/>
/// there. See <c>Doc/Architecture/NoStaticState</c>.</para>
///
/// <para>🚨 <b>Bounded, and the bound is VISIBLE rather than silent.</b> One entry per distinct
/// stream id served, and a long-lived owner on a written path mints fresh ones (the change-feed
/// eviction / re-lease cycle documented on <c>Workspace._remoteStreamLeases</c>). So the ledger
/// prunes its oldest entries past <see cref="Capacity"/> — and the instant it has pruned ANYTHING,
/// an absent id answers <see cref="LostStreamCause.NoRecord"/> rather than
/// <see cref="LostStreamCause.NeverRegistered"/>, because the two are no longer distinguishable.
/// A diagnostic that cannot fail to give an answer is not a diagnostic.</para>
/// </summary>
internal sealed class SyncStreamActivationLedger
{
    /// <summary>
    /// How many stream ids this hub keeps the disposition of. Sized for "how many streams was this
    /// hub serving around the time of the refusal", which is the only question the ledger answers:
    /// a refusal names a stream that ended recently (the click raced its teardown), so recency is
    /// what has to survive. NOT a correctness bound — running past it costs the
    /// <see cref="LostStreamCause.NeverRegistered"/>/<see cref="LostStreamCause.NoRecord"/>
    /// distinction on that hub and nothing else.
    /// </summary>
    private const int Capacity = 512;

    /// <summary>
    /// How far past <see cref="Capacity"/> the ledger is allowed to run before a prune pass. A
    /// prune is one O(n) sweep, so the slack is what makes it amortize to one sweep per
    /// <see cref="Slack"/> new streams instead of one per new stream once full.
    /// </summary>
    private const int Slack = 128;

    /// <summary>
    /// The dispositions, keyed by stream id. <see cref="ConcurrentDictionary{TKey,TValue}"/> is the
    /// sanctioned exception to the immutable-collections rule for concurrent mutation, and this is
    /// an instance field on a hub-scoped singleton — never static.
    /// </summary>
    private readonly ConcurrentDictionary<string, Disposition> dispositions = new(StringComparer.Ordinal);

    private long sequence;
    private long pruned;

    /// <summary>Records that a <c>sync/{streamId}</c> sub-hub was registered on this hub activation.</summary>
    internal void RecordSyncHubRegistered(string? streamId)
    {
        if (string.IsNullOrEmpty(streamId))
            return;
        var seq = Interlocked.Increment(ref sequence);
        dispositions.AddOrUpdate(
            streamId,
            _ => new Disposition(seq),
            (_, existing) => existing);
        PruneIfOverflowing(seq);
    }

    /// <summary>
    /// Records that an <see cref="UnsubscribeRequest"/> for <paramref name="streamId"/> reached this
    /// hub.
    ///
    /// <para>🚨 Deliberately does NOT create an entry. An unsubscribe for a stream this activation
    /// never served says something about the SUBSCRIBER's lifetime, not about ours — recording it
    /// would turn the unambiguous "this activation never served that stream" into "the subscriber
    /// released it", which is the exact conflation this ledger exists to end, and would let a
    /// stranger's stream id consume a slot.</para>
    /// </summary>
    internal void RecordUnsubscribeReceived(string? streamId)
    {
        if (string.IsNullOrEmpty(streamId))
            return;
        if (dispositions.TryGetValue(streamId, out var existing))
            existing.MarkReleasedBySubscriber();
    }

    /// <summary>
    /// Says which of <see cref="LostStreamCause"/> applies to <paramref name="streamId"/> — asked
    /// only when a stream message for it is already being refused.
    /// </summary>
    internal LostStreamCause Classify(string? streamId)
    {
        if (string.IsNullOrEmpty(streamId))
            return LostStreamCause.NoRecord;
        if (dispositions.TryGetValue(streamId, out var disposition))
            return disposition.ReleasedBySubscriber
                ? LostStreamCause.ReleasedBySubscriber
                : LostStreamCause.ReapedByOwner;
        // Absent. Definitive only while nothing has aged out — see the class remarks.
        return Interlocked.Read(ref pruned) == 0
            ? LostStreamCause.NeverRegistered
            : LostStreamCause.NoRecord;
    }

    /// <summary>How many stream dispositions this ledger currently holds — the denominator a
    /// <see cref="LostStreamCause.NoRecord"/> line states.</summary>
    internal int Held => dispositions.Count;

    /// <summary>How many dispositions have aged out of this ledger — the other half of that
    /// denominator, and what makes <see cref="LostStreamCause.NoRecord"/> reachable at all.</summary>
    internal long Pruned => Interlocked.Read(ref pruned);

    /// <summary>
    /// Drops the entries older than the newest <see cref="Capacity"/>, in one pass, once the ledger
    /// has run <see cref="Slack"/> past its capacity. Lock-free and never waits: two threads that
    /// prune at once remove the same entries, and <c>TryRemove</c> is idempotent, so the only
    /// consequence is that <see cref="pruned"/> counts each removal once — which is all
    /// <see cref="Classify"/> reads it for (has anything aged out at all).
    /// </summary>
    private void PruneIfOverflowing(long newestSequence)
    {
        if (dispositions.Count <= Capacity + Slack)
            return;
        var cutoff = newestSequence - Capacity;
        foreach (var entry in dispositions)
        {
            if (entry.Value.Sequence > cutoff)
                continue;
            if (dispositions.TryRemove(entry))
                Interlocked.Increment(ref pruned);
        }
    }

    /// <summary>
    /// 🚨 A CLASS, not a record or a struct: the dictionary holds it by reference so
    /// <see cref="MarkReleasedBySubscriber"/> reaches the stored entry without a read-modify-write
    /// that could lose a concurrent registration.
    /// </summary>
    private sealed class Disposition(long sequence)
    {
        private int releasedBySubscriber;

        /// <summary>Registration order — what <see cref="PruneIfOverflowing"/> ages by.</summary>
        public long Sequence { get; } = sequence;

        /// <summary>Whether an <see cref="UnsubscribeRequest"/> for this stream reached the hub.</summary>
        public bool ReleasedBySubscriber => Volatile.Read(ref releasedBySubscriber) != 0;

        public void MarkReleasedBySubscriber() => Volatile.Write(ref releasedBySubscriber, 1);
    }

    /// <summary>
    /// The ledger of <paramref name="hub"/>, or <see langword="null"/> when it has none to give —
    /// a hub whose DI scope has already closed, which is routine on the paths that read this (a
    /// refusal fires while a hub is tearing down). Resolving a diagnostic must never itself become
    /// a fault.
    /// </summary>
    internal static SyncStreamActivationLedger? For(IMessageHub? hub)
    {
        try
        {
            return hub?.ServiceProvider.GetService<SyncStreamActivationLedger>();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
