using System.Reactive;
using System.Reactive.Linq;
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
    /// and RESUMED, which a database restart always produces — and each commit announced in
    /// <paramref name="partition"/>, since a commit is announced only after the store accepted it.
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
    {
        if (feed is null)
            return Observable.Never<Unit>();
        // The handler is forwarded as-is and the filter runs as an operator, so a fault in it reaches
        // the subscriber's OnError instead of being thrown out of the feed's delivery callback.
        var commits = Observable.Create<MeshChangeEvent>(observer => feed.Subscribe(observer.OnNext))
            .Where(change => partition is null || InPartition(change.Path, partition))
            .Select(_ => Unit.Default);
        return commits.Merge(feed.Gaps.Select(_ => Unit.Default));
    }

    /// <summary>
    /// Re-issues a write that was answered <c>Unavailable</c> — "not attempted, nothing written,
    /// retrying the same request is meaningful" — once the store is evidenced to answer again (#7072).
    /// The ONE shape for every such site: the caller supplies the SAME request (same node id), how
    /// to tell an Unavailable outcome, and the partition the write lands in.
    ///
    /// <para><b>How it waits.</b> The first re-issue waits for <see cref="StoreAnswersAgain"/> —
    /// a feed that resumed, or a commit in the partition. If THAT re-issue is answered Unavailable
    /// again, a commit in the partition has just been shown not to be evidence for this write (the
    /// store took someone else's commit and still never saw ours), so every later re-issue waits for
    /// the feed to RESUME instead — a reconnect, which is rare. That is what keeps this from turning a
    /// busy partition into a retry loop, without any count or clock.</para>
    ///
    /// <para><b>What it never does.</b> It never re-issues a write that was JUDGED: an outcome
    /// <paramref name="wasUnavailable"/> rejects is emitted and the sequence completes, so a refusal
    /// stays a named failure. And it never issues the FIRST attempt — the caller made it, and is here
    /// because it came back Unavailable.</para>
    /// </summary>
    /// <typeparam name="T">The write's outcome.</typeparam>
    /// <param name="feed">The mesh's invalidation feed, or <c>null</c> (then nothing is re-issued).</param>
    /// <param name="partition">The partition the write lands in.</param>
    /// <param name="attempt">The SAME write, cold — subscribed once per re-issue.</param>
    /// <param name="wasUnavailable">True when an outcome means "not attempted, retry meaningful".</param>
    /// <param name="onStillUnavailable">Told about each re-issue that was answered Unavailable again.</param>
    /// <returns>The first outcome that is not Unavailable; never completes while the store stays away.</returns>
    public static IObservable<T> ReissueWhenStoreAnswers<T>(
        this IMeshInvalidationFeed? feed,
        string partition,
        Func<IObservable<T>> attempt,
        Func<T, bool> wasUnavailable,
        Action<T>? onStillUnavailable = null)
    {
        if (feed is null)
            return Observable.Never<T>();
        var reconnects = feed.Gaps.Select(_ => Unit.Default);
        IObservable<T> After(IObservable<Unit> evidence) =>
            evidence
                .Take(1)
                .SelectMany(_ => Observable.Defer(attempt).Take(1))
                .SelectMany(outcome =>
                {
                    if (!wasUnavailable(outcome))
                        return Observable.Return(outcome);
                    onStillUnavailable?.Invoke(outcome);
                    return Observable.Defer(() => After(reconnects));
                });
        return Observable.Defer(() => After(feed.StoreAnswersAgain(partition)));
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
