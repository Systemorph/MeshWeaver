using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Mesh;

/// <summary>
/// Tells <b>"the store could not be REACHED"</b> apart from <b>"the operation was REFUSED"</b> — the
/// second distinction a node-operation handler's catch-all cannot make on its own, and the sibling of
/// <see cref="CancellationClassifier"/>.
///
/// <para>🚨 <b>Why this is not a log-level preference</b> (#3050/#3051). A transient database-connect
/// timeout arriving at a create handler's terminal error arm was answered
/// <see cref="NodeCreationRejectionReason.Unknown"/> under the sentence <c>"Unexpected error during
/// node creation"</c>. Both halves are wrong in the same direction, and they are wrong about
/// DIFFERENT audiences:</para>
///
/// <list type="bullet">
///   <item><b>The operator</b> reads "unexpected error during node creation" and goes hunting for a
///     defect in the create path. The create path was fine; a database was unreachable for a few
///     seconds. This is the same wording defect #2876 fixed one layer up, where an area whose store
///     was unreachable rendered the driver's own text under <c>"Rendering failed for area Catalog"</c>
///     — naming the innocent component as the thing that failed.</item>
///   <item><b>The caller</b> reads a rejection reason that is indistinguishable from a verdict. That
///     matters because the two demand opposite responses: a create that was REFUSED must not be
///     retried, while a create that was never ATTEMPTED must be — with the SAME node id. A caller
///     that reads "refused" and mints a fresh id on its next attempt writes a DUPLICATE, which is
///     #2229's shape exactly.</item>
/// </list>
///
/// <para><b>What this is deliberately NOT: a retry.</b> The bounded retry already exists and already
/// ran — <c>TransientStorageFaults.RetryTransientConnect</c> (#2521) wraps the storage-backed
/// observables at 250 → 500 → 1000 ms before the last error is surfaced. A fault that reaches a
/// handler's terminal arm is one whose budget is honestly spent, so retrying it HERE would be a
/// second retry aimed at the resource that is already the bottleneck — the same non-choice #3031
/// states for the render path, and the band-aid AGENTS.md forbids. The answer to a spent budget is to
/// report the condition accurately, not to spend more of someone else's.</para>
///
/// <para><b>One rule, three consumers.</b> The predicate forwards to
/// <see cref="StorageFaults.IsTransientConnectFault"/> in <c>MeshWeaver.Data.Contract</c> — the one
/// assembly the query fan-in, the layout renderer and the node-operation handlers can all see. A
/// second copy would drift silently in either direction: a fault the fan-in retries but a create
/// reports as a defect, or an outage a create excuses that the fan-in never retried.</para>
/// </summary>
public static class StoreReachability
{
    /// <summary>
    /// True when <paramref name="exception"/> means the data store could not be REACHED — a transient
    /// driver connect/timeout fault, so the operation was never evaluated and nothing was written.
    ///
    /// <para>The matched class is <see cref="StorageFaults.IsTransientConnectFault"/>'s: a
    /// <see cref="System.Data.Common.DbException"/> carrying a connection-class SQLSTATE, or one
    /// wrapping a network-level <see cref="TimeoutException"/> /
    /// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/> — the shape of
    /// Npgsql's <c>"Failed to connect to 10.42.18.4:5432 ---&gt; TimeoutException: Timeout during
    /// connection attempt"</c>. A real query/schema error (<c>42P01</c>, <c>23505</c>) is NOT matched
    /// and keeps its loud "unexpected" treatment: excusing a defect as an outage would hide it, which
    /// is the mirror image of the bug this classifier fixes.</para>
    /// </summary>
    /// <param name="exception">The exception a handler's error branch received; may be null.</param>
    public static bool IsStoreUnreachable(Exception? exception)
        => StorageFaults.IsTransientConnectFault(exception);

    /// <summary>
    /// The ONE sentence every node operation answers with when <see cref="IsStoreUnreachable"/> is
    /// true — so a user, an agent and a test all read the same words, and the bulk sibling cannot
    /// drift from the singular one.
    ///
    /// <para>It says three things on purpose: the operation was <b>not attempted</b>, <b>nothing was
    /// written</b>, and <b>retrying the same request is meaningful</b>. The last clause is the one
    /// that keeps a caller from minting a new id for its next attempt (#2229).</para>
    /// </summary>
    /// <param name="operation">The operation, as a sentence subject — e.g. <c>"Node creation at 'x/y'"</c>.</param>
    /// <returns>A message safe to hand to a caller, a log line and an activity alike.</returns>
    public static string DescribeNotAttempted(string operation)
        => $"{operation} could not be attempted: the data store was unreachable. Nothing was written — "
           + "this is an availability failure, not a refusal, so retrying the same request "
           + "(with the same node id) is meaningful.";

    /// <summary>
    /// The same availability verdict for a write that had ALREADY STARTED when the store became
    /// unreachable — so part of the batch may have landed.
    ///
    /// <para>🚨 Why this is a separate sentence rather than a nuance in the one above.
    /// "Nothing was written" is a claim ABOUT THE DATA, and it is only true when the failure
    /// preceded the write. Saying it after a partial landing tells a caller the store is in a state
    /// it is not — and the natural next step, retrying the whole batch, then double-writes whatever
    /// did land. A wrong claim about durability is worse than an vague one, so when we do not know,
    /// we say we do not know.</para>
    /// </summary>
    /// <param name="operation">The operation, as a sentence subject.</param>
    /// <returns>A message safe to hand to a caller, a log line and an activity alike.</returns>
    public static string DescribeMayHavePartiallyLanded(string operation)
        => $"{operation} failed part-way: the data store became unreachable after the write had "
           + "started, so an UNKNOWN subset may already be durable. This is an availability failure, "
           + "not a refusal — but read the current state before retrying, because re-sending the "
           + "whole request could duplicate whatever landed.";

    // ── What a caller DOES with "retrying is meaningful" (#7072) ─────────────────────────────────
    //
    // The sentence above tells a caller that a retry with the same id is meaningful, and until #7072
    // nothing in the platform said WHEN. A NodeType release re-cut refused this way during a 40-second
    // database restart (#7068) was never re-issued by the pod that stamped it, although the pod stayed
    // up and ready all night: the release came 76 minutes to 3 hours later, from an unrelated event.
    // The answer is not a timer that retries in case the store is back (the band-aid this class
    // already refuses above), but a FACT that it is.

    /// <summary>
    /// The EVIDENCE that the store answers again, as this process can observe it (#7072): each
    /// declared <see cref="ChangeFeedGap"/> — the backend's cross-process feed lost its connection
    /// and RESUMED, which a database restart always produces — and each commit THE STORE announced
    /// in <paramref name="partition"/> (<see cref="IMeshInvalidationFeed.DurableCommits"/>), since
    /// such a commit is announced only after the store accepted it. An optimistic notification
    /// published before persistence is not evidence and is not counted.
    ///
    /// <para>🚨 Facts, never a clock. Nothing here fires because time passed; while the store is
    /// unreachable no commit lands and no feed resumes, so the signal stays silent exactly as long as
    /// re-issuing a write would be pointless. Never emits on a host without an invalidation feed —
    /// such a host has one process and no cross-process store to lose.</para>
    /// </summary>
    /// <param name="feed">The mesh's invalidation feed, or <c>null</c>.</param>
    /// <param name="partition">The partition whose commits count (its first path segment), or
    /// <c>null</c> to count every commit.</param>
    /// <returns>An infinite signal; one element per piece of evidence.</returns>
    public static IObservable<Unit> StoreAnswersAgain(this IMeshInvalidationFeed? feed, string? partition)
        => EvidenceBySource(feed, partition).Select(_ => Unit.Default);

    /// <summary>
    /// <see cref="StoreAnswersAgain"/> with its SOURCE kept: <c>true</c> for a declared gap (the feed
    /// resumed), <c>false</c> for a commit in the partition. The two are different kinds of evidence —
    /// a commit can be shown not to count for one particular write, a reconnect cannot — so a caller
    /// that weighs them must be able to tell which fired.
    /// </summary>
    private static IObservable<bool> EvidenceBySource(IMeshInvalidationFeed? feed, string? partition)
    {
        if (feed is null)
            return Observable.Never<bool>();
        // 🚨 DURABLE commits only (review on #7087). The invalidation feed also carries optimistic
        // notifications published before persistence; one of those starting a re-issue that is
        // answered Unavailable would disprove commits for good, and the durable commit that follows
        // when the store's write connections recover could then never start the next one.
        var commits = feed.DurableCommits
            .Where(change => partition is null || InPartition(change.Path, partition))
            .Select(_ => false);
        return commits.Merge(feed.Gaps.Select(_ => true));
    }

    /// <summary>
    /// Re-issues a write that was answered <c>Unavailable</c> — "not attempted, nothing written,
    /// retrying the same request is meaningful" — once the store is evidenced to answer again (#7072).
    /// The ONE shape for every such site: the caller supplies the SAME request (same node id), how
    /// to tell an Unavailable outcome, and the partition the write lands in.
    ///
    /// <para><b>How it waits.</b> A re-issue starts on the first piece of
    /// <see cref="StoreAnswersAgain"/> evidence — a feed that resumed, or a commit in the partition.
    /// The evidence is observed for the WHOLE life of the sequence, never only between attempts:
    /// evidence that arrives while an attempt is in flight is RETAINED (the event that started the
    /// attempt is not), and if the attempt is answered Unavailable it starts the next one at once. A
    /// feed's gaps are not replayed, so a reconnect that landed during the attempt would otherwise be
    /// lost for good.</para>
    ///
    /// <para><b>Which evidence still counts.</b> A re-issue STARTED BY A COMMIT that is answered
    /// Unavailable again shows that a commit in the partition is not evidence for this write (the
    /// store took someone else's commit and still never saw ours), so from then on only a reconnect
    /// starts a re-issue. A re-issue started by a reconnect that fails disproves nothing about commits,
    /// and commits keep counting. That is what keeps a busy partition from becoming a retry loop,
    /// without any count or clock.</para>
    ///
    /// <para><b>What it never does.</b> It never re-issues a write that was JUDGED: an outcome
    /// <paramref name="wasUnavailable"/> rejects is emitted and the sequence completes, so a refusal
    /// stays a named failure. It never runs two attempts at once. And it never issues the FIRST
    /// attempt — the caller made it, and is here because it came back Unavailable.</para>
    ///
    /// <para><b>Evidence the caller already holds.</b> A caller that learns of the obligation only
    /// from a record the store accepted AFTER the write failed (a stamp written after the refused
    /// create) holds evidence the feed will never replay: a reconnect or a commit that preceded the
    /// subscription is gone. It passes <paramref name="storeAnsweredBeforeSubscription"/>, and the
    /// first re-issue starts at once (review on #7087). That evidence disproves nothing if the
    /// re-issue is answered Unavailable — the record may have been accepted locally ahead of the
    /// store — so commits keep counting, exactly as after a reconnect-started failure.</para>
    /// </summary>
    /// <typeparam name="T">The write's outcome.</typeparam>
    /// <param name="feed">The mesh's invalidation feed, or <c>null</c> (then nothing is re-issued).</param>
    /// <param name="partition">The partition the write lands in.</param>
    /// <param name="attempt">The SAME write, cold — subscribed once per re-issue.</param>
    /// <param name="wasUnavailable">True when an outcome means "not attempted, retry meaningful".</param>
    /// <param name="onStillUnavailable">Told about each re-issue that was answered Unavailable again,
    /// with whether a commit in the partition still starts the next one (<c>false</c>: only a
    /// reconnect does, because this failure was started by a commit).</param>
    /// <param name="storeAnsweredBeforeSubscription">The caller holds evidence, from before this
    /// subscription, that the store answered after the write failed: start the first re-issue at once.</param>
    /// <returns>The first outcome that is not Unavailable; never completes while the store stays away.</returns>
    public static IObservable<T> ReissueWhenStoreAnswers<T>(
        this IMeshInvalidationFeed? feed,
        string partition,
        Func<IObservable<T>> attempt,
        Func<T, bool> wasUnavailable,
        Action<T, bool>? onStillUnavailable = null,
        bool storeAnsweredBeforeSubscription = false)
    {
        if (feed is null)
            return Observable.Never<T>();
        return Observable.Create<T>(observer =>
        {
            // Every transition runs inside the ONE merged subscription below, which Rx serialises, so
            // this state is touched by one callback at a time. An attempt that answers synchronously
            // re-enters that callback; every transition therefore finishes its state change BEFORE it
            // starts an attempt, so a re-entrant call always sees a consistent state.
            var state = new ReissueState();
            var outcomes = new Subject<(T Outcome, bool ByGap)>();
            var inFlight = new SerialDisposable();

            void Start(bool byGap)
            {
                // 🚨 Never start a write once the subscription is disposed (review on #7087). A
                // callback may dispose it and then return into this path with evidence retained;
                // assigning a slot to a disposed SerialDisposable disposes the slot, but only AFTER
                // the cold write below would already have been subscribed.
                if (state.Stopped)
                    return;
                state.InFlight = true;
                state.PendingGap = false;
                state.PendingCommit = false;
                var slot = new SingleAssignmentDisposable();
                inFlight.Disposable = slot;
                var answered = false;
                slot.Disposable = Observable.Defer(attempt).Take(1).Subscribe(
                    outcome =>
                    {
                        answered = true;
                        outcomes.OnNext((outcome, byGap));
                    },
                    observer.OnError,
                    () =>
                    {
                        if (!answered)
                            observer.OnError(new InvalidOperationException(
                                "A re-issued write completed without an outcome, so whether the store "
                                + "saw it is unknown — refusing to wait on evidence for an answer that "
                                + "will never come."));
                    });
            }

            // 🚨 The OUTCOMES are connected FIRST (review on #7087). Merge subscribes its sources in
            // order, and a feed may deliver evidence while it is being subscribed; with the evidence
            // first, that evidence ran Start before the hot `outcomes` subject had a subscriber, an
            // attempt that answered in that window lost its outcome, and InFlight stayed true for good
            // — every later piece of evidence was retained and none ever started an attempt.
            // Evidence the caller holds from before the subscription is delivered LAST, as the
            // non-disproving kind (see the summary): after the outcomes AND the live feed are
            // connected, through the same serialised Merge (review on #7087). StartWith emitted it
            // before the live feed was subscribed, so a reconnect that arrived while that first
            // attempt was being subscribed was lost to the non-replaying feed, and an Unavailable
            // answer then left an idle partition with nothing to start the next attempt.
            var evidence = EvidenceBySource(feed, partition);
            if (storeAnsweredBeforeSubscription)
                evidence = evidence.Merge(Observable.Return(true));
            var events = outcomes.Select(x => (IsOutcome: true, x.ByGap, Outcome: (T?)x.Outcome))
                .Merge(evidence.Select(byGap => (IsOutcome: false, ByGap: byGap, Outcome: default(T))));

            var subscription = events.Subscribe(e =>
            {
                if (state.Done || state.Stopped)
                    return;
                if (!e.IsOutcome)
                {
                    if (!e.ByGap && !state.CommitsCount)
                        return;
                    if (state.InFlight)
                    {
                        // Retained: newer than the event that started the attempt in flight.
                        if (e.ByGap) state.PendingGap = true;
                        else state.PendingCommit = true;
                        return;
                    }
                    Start(e.ByGap);
                    return;
                }

                // 🚨 The attempt stays IN FLIGHT until its outcome is fully weighed (review on #7087).
                // Merge allows synchronous re-entry, so a commit published from inside wasUnavailable
                // or onStillUnavailable arrives HERE before this branch finishes; marked idle, it
                // started a nested attempt before a commit-started failure had disproved commits, and
                // that nested Start cleared any retained reconnect — an Unavailable answer to it then
                // left the write waiting for a reconnect that had already happened. In flight, the
                // re-entrant evidence is retained and judged below like any other.
                var outcome = e.Outcome!;
                if (!wasUnavailable(outcome))
                {
                    state.Done = true;
                    state.InFlight = false;
                    observer.OnNext(outcome);
                    observer.OnCompleted();
                    return;
                }
                onStillUnavailable?.Invoke(outcome, state.CommitsCount && e.ByGap);
                if (!e.ByGap)
                {
                    // A commit started it and the store still never saw it: commits are disproved.
                    state.CommitsCount = false;
                    state.PendingCommit = false;
                }
                state.InFlight = false;
                if (state.PendingGap)
                    Start(byGap: true);
                else if (state.PendingCommit)
                    Start(byGap: false);
            }, observer.OnError);

            // The stop flag goes FIRST, so the state machine is stopped before anything it owns is
            // torn down — including a disposal that happens from inside one of its own callbacks.
            return new CompositeDisposable(
                Disposable.Create(() => state.Stopped = true), subscription, inFlight, outcomes);
        });
    }

    /// <summary>The state of one <see cref="ReissueWhenStoreAnswers{T}"/> subscription — owned by
    /// that subscription alone and touched only from its serialised callback.</summary>
    private sealed class ReissueState
    {
        public bool InFlight;
        public bool CommitsCount = true;
        public bool PendingGap;
        public bool PendingCommit;
        public bool Done;

        /// <summary>Set when the subscription is disposed; read on every transition, from any thread.</summary>
        public volatile bool Stopped;
    }

    private static bool InPartition(string? path, string partition)
        => !string.IsNullOrEmpty(path)
           && string.Equals(path.Trim('/').Split('/', 2)[0], partition.Trim('/'),
               StringComparison.OrdinalIgnoreCase);

    // ── The same verdicts, keyed (#4507) ───────────────────────────────────────────────────────
    //
    // 🚨 The ENGLISH half is composed by the two methods above, never retyped. That is what keeps
    // the promise this class exists for — "so a user, an agent and a test all read the same words"
    // — true across the localization seam as well: a reword of the sentence moves the fallback and
    // the log line together, and only the catalog templates have to follow. A second literal here
    // would be the drift the class was created to prevent.

    /// <summary>The catalog key for <see cref="NodeCreationNotAttempted"/>.</summary>
    public const string NodeCreationNotAttemptedKey = "activity.node.create.storeUnreachable";

    /// <summary>The catalog key for <see cref="BulkCreationNotAttempted"/>.</summary>
    public const string BulkCreationNotAttemptedKey = "activity.node.bulkCreate.storeUnreachable";

    /// <summary>The catalog key for <see cref="BulkCreationMayHavePartiallyLanded"/>.</summary>
    public const string BulkCreationPartiallyLandedKey = "activity.node.bulkCreate.storePartiallyLanded";

    /// <summary>
    /// <see cref="DescribeNotAttempted"/> for a SINGULAR create, carried with the catalog key that
    /// renders it in the viewer's language.
    /// </summary>
    /// <param name="path">The path the create targeted.</param>
    /// <returns>The keyed verdict.</returns>
    public static LocalizableText NodeCreationNotAttempted(string path)
        => LocalizableText.Keyed(
            DescribeNotAttempted($"Node creation at '{path}'"),
            NodeCreationNotAttemptedKey, ("path", path));

    /// <summary>
    /// <see cref="DescribeNotAttempted"/> for a BULK create, carried with its catalog key.
    /// </summary>
    /// <param name="count">How many nodes the request carried — the REQUEST's count, never the
    /// attempted one (see the call site's note).</param>
    /// <returns>The keyed verdict.</returns>
    public static LocalizableText BulkCreationNotAttempted(int count)
        => LocalizableText.Keyed(
            DescribeNotAttempted($"Bulk creation of {count} node(s)"),
            BulkCreationNotAttemptedKey, ("count", count));

    /// <summary>
    /// <see cref="DescribeMayHavePartiallyLanded"/> for a BULK create, carried with its catalog key.
    /// </summary>
    /// <param name="count">How many nodes the request carried.</param>
    /// <returns>The keyed verdict.</returns>
    public static LocalizableText BulkCreationMayHavePartiallyLanded(int count)
        => LocalizableText.Keyed(
            DescribeMayHavePartiallyLanded($"Bulk creation of {count} node(s)"),
            BulkCreationPartiallyLandedKey, ("count", count));
}
