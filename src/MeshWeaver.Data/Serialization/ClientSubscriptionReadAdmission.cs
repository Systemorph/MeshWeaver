using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Decides, for ONE owner-side client subscription, whether its subscriber may still read what the
/// owner is about to send it (MeshWeaver#4306, follow-up 2). Registered on a hub's configuration
/// (<see cref="ClientSubscriptionReadAdmissionExtensions.WithClientSubscriptionReadAdmission"/>) by
/// the layer that owns the access model; the data layer itself knows nothing of permissions.
///
/// <para>Asked once, when the owner builds the server-side stream for a <see cref="SubscribeRequest"/>
/// the delivery gate has already admitted. <c>null</c> means the subscription is not followed: its
/// subscribe-time admission stands for its lifetime. Otherwise the answer maps one revision the owner
/// is about to forward to the LIVE verdict for that revision. The owner forwards a revision only once
/// its own verdict has admitted it, and ends the subscription with a refusal the moment a verdict is
/// false (<see cref="RevisionAdmission"/>.<c>AdmitEachRevision</c>).</para>
/// </summary>
/// <param name="owner">The owning hub that serves the subscription.</param>
/// <param name="delivery">The admitted subscribe delivery, with the subscriber's context.</param>
/// <returns>Null when nothing is followed; otherwise how each served revision is decided.</returns>
public delegate ClientReadFollow? ClientSubscriptionReadAdmission(
    IMessageHub owner, IMessageDelivery<SubscribeRequest> delivery);

/// <summary>How a followed client subscription decides each revision it serves.</summary>
/// <param name="VerdictOf">The live verdict for one served revision (the reduced value, e.g. the node itself).</param>
/// <param name="FirstVerdictBudget">How long a revision's first verdict may take; a stalled one is a refusal.</param>
/// <param name="Lifetime">What the follow holds for THIS subscription (a shared verdict's connection), released
/// when the followed sequence ends, however it ends — never registered on the owning hub, whose
/// registrations live as long as the hub does (review of #7016, thread 4338167103). Null when it holds nothing.</param>
public sealed record ClientReadFollow(
    Func<object?, IObservable<bool>> VerdictOf, TimeSpan FirstVerdictBudget, IDisposable? Lifetime = null);

/// <summary>Registers the <see cref="ClientSubscriptionReadAdmission"/> a hub's owner-side subscriptions follow.</summary>
public static class ClientSubscriptionReadAdmissionExtensions
{
    /// <summary>Sets the read admission that every new owner-side client subscription of this hub follows.</summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="admission">The admission.</param>
    /// <returns>The configuration carrying it.</returns>
    public static MessageHubConfiguration WithClientSubscriptionReadAdmission(
        this MessageHubConfiguration config, ClientSubscriptionReadAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return config.Set(admission);
    }
}

/// <summary>
/// The end of a followed read on which NO VERDICT was reached (MeshWeaver#7825, #7830): the check
/// stalled past its budget, faulted, or answered nothing. Raised as a <see cref="DeliveryFailureException"/>
/// carrying <see cref="ErrorType.Unavailable"/> — the platform's one spelling of "we could not find
/// out" — so every classifier reads it as NO VERDICT and none of them as a denial.
///
/// <para>🚨 <b>Why it is not the refusal.</b> A followed read used to end every undecided revision
/// with the read gate's own DENIAL ("Access denied: user … lacks Read permission on …"). A thread-pool
/// stall, or a store that could not open a connection during a re-check, therefore told a correctly
/// entitled user — and the operator reading the log — that their rights had been withdrawn, on every
/// view of the page at once. The read still ends (fail closed: no revision is forwarded on a verdict
/// nobody reached), but it ends saying what happened. See Doc/Architecture/UndeterminedIsNotNo.</para>
/// </summary>
public static class ReadVerdict
{
    /// <summary>The exception that ends a read whose verdict was not reached.</summary>
    /// <param name="subject">What could not be decided, e.g. <c>user 'u' may keep reading 'p'</c>.</param>
    /// <param name="cause">Why: the fault, the stall's <see cref="TimeoutException"/>, or null when the check answered nothing.</param>
    /// <returns>A <see cref="DeliveryFailureException"/> carrying <see cref="ErrorType.Unavailable"/>.</returns>
    public static Exception NoVerdict(string subject, Exception? cause)
    {
        var because = cause is null ? "the check answered nothing" : $"{cause.GetType().Name}: {cause.Message}";
        return new DeliveryFailureException(new DeliveryFailure(null!,
            $"Permission check unavailable — no verdict was reached on whether {subject}, so the read has ended. "
            + $"This is NOT a statement about this user's rights. Retry shortly. Cause: {because}")
        {
            ErrorType = ErrorType.Unavailable,
            ExceptionType = cause?.GetType().FullName ?? string.Empty,
        });
    }

    /// <summary>True when <paramref name="error"/> is the end of a read on which no verdict was reached.</summary>
    /// <param name="error">The error that ended a read.</param>
    /// <returns>Whether it carries <see cref="ErrorType.Unavailable"/>.</returns>
    public static bool IsNoVerdict(Exception? error)
        => error is DeliveryFailureException { Failure.ErrorType: ErrorType.Unavailable };
}

/// <summary>
/// Couples every forwarded revision to its OWN admission decision (MeshWeaver#4306, follow-up 1).
/// </summary>
public static class RevisionAdmission
{
    /// <summary>
    /// The five-argument <c>AdmitEachRevision</c> with the default no-verdict end
    /// (<see cref="ReadVerdict.NoVerdict"/>, naming no subject). Kept with its original signature, so
    /// a module compiled against it still binds.
    /// </summary>
    /// <typeparam name="T">The revision type.</typeparam>
    /// <param name="revisions">The revisions to forward.</param>
    /// <param name="verdictOf">The live verdict for one revision; cold, subscribed once per revision.</param>
    /// <param name="firstVerdictBudget">How long a revision's first verdict may take.</param>
    /// <param name="refusal">The error that ends the sequence when a verdict is false.</param>
    /// <returns>The admitted revisions, ending once one is refused or undecided.</returns>
    public static IObservable<T> AdmitEachRevision<T>(
        this IObservable<T> revisions,
        Func<T, IObservable<bool>> verdictOf,
        TimeSpan firstVerdictBudget,
        Func<Exception> refusal)
        => revisions.AdmitEachRevision(verdictOf, firstVerdictBudget, refusal,
            cause => ReadVerdict.NoVerdict("this read may continue", cause));

    /// <summary>
    /// Forwards each revision of <paramref name="revisions"/> only after the verdict decided FOR THAT
    /// REVISION admits it, in order, and ends the sequence as soon as any verdict refuses it: with
    /// <paramref name="refusal"/> when the verdict DECIDED <c>false</c>, with
    /// <paramref name="noVerdict"/> when no verdict was reached.
    ///
    /// <para>🚨 <b>Why per revision.</b> A switch on ONE live verdict forwarded the stream while the
    /// verdict was true and cut it when the verdict turned false. A revision that ITSELF ends the
    /// admission (a revocation stamp, a changed audience) then reached the caller first, because its
    /// verdict could only be decided after it arrived: for a stored type the declaration is looked up
    /// asynchronously. Here a revision waits for its own verdict, so the revision that ends the read is
    /// never delivered.</para>
    ///
    /// <para><b>Nothing is dropped and nothing is reordered.</b> A later revision queues behind an
    /// earlier one whose verdict is still pending, so a patch chain stays contiguous. The sequence
    /// either forwards a revision or terminates. A verdict that emits synchronously forwards
    /// synchronously.</para>
    ///
    /// <para><b>Live.</b> After a revision is admitted its verdict keeps being followed until the next
    /// revision is admitted, so an expiry or a removed audience member ends the sequence even when
    /// nothing else emits. Each revision's FIRST verdict is bounded by
    /// <paramref name="firstVerdictBudget"/>.</para>
    ///
    /// <para>🚨 <b>Undecided ends the read, but never as a denial</b> (MeshWeaver#7825). A verdict
    /// that stalls past the budget, FAULTS or completes without an answer still ends the sequence —
    /// nothing is forwarded on a verdict nobody reached (fail closed) — with
    /// <paramref name="noVerdict"/>, never with <paramref name="refusal"/>: the denial names a user and
    /// a permission the check never decided about.</para>
    /// </summary>
    /// <typeparam name="T">The revision type.</typeparam>
    /// <param name="revisions">The revisions to forward.</param>
    /// <param name="verdictOf">The live verdict for one revision; cold, subscribed once per revision.
    /// An ERROR on it means no verdict was reached.</param>
    /// <param name="firstVerdictBudget">How long a revision's first verdict may take.</param>
    /// <param name="refusal">The error that ends the sequence when a verdict is false.</param>
    /// <param name="noVerdict">The error that ends the sequence when no verdict was reached, from its
    /// cause: the fault, the stall's <see cref="TimeoutException"/>, or null for an empty answer.</param>
    /// <returns>The admitted revisions, ending once one is refused or undecided.</returns>
    public static IObservable<T> AdmitEachRevision<T>(
        this IObservable<T> revisions,
        Func<T, IObservable<bool>> verdictOf,
        TimeSpan firstVerdictBudget,
        Func<Exception> refusal,
        Func<Exception?, Exception> noVerdict)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(verdictOf);
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(noVerdict);
        Exception EndOf(Decision decision) => decision.Undecided ? noVerdict(decision.Cause) : refusal();
        return Observable.Create<T>(observer =>
        {
            // Every per-revision verdict subscription is owned HERE, registered before it connects
            // (review of #7016, thread 4336694680): an unsubscribe that lands while a verdict is being
            // set up — before its revision reaches the pipeline below — still releases it.
            var owned = new System.Reactive.Disposables.CompositeDisposable();
            // The index of the newest ADMITTED revision. A revision is followed until a NEWER one has
            // been admitted — never merely until one has arrived — so there is no hand-over gap while
            // the newer revision's first verdict is still being decided (thread 4336475629).
            var admitted = new BehaviorSubject<long>(-1);
            var pipeline = revisions
                // Each revision's verdict is subscribed the moment the revision ARRIVES, so it is
                // decided while the previous one is still followed, and its first refusal is latched
                // then, whatever the verdict says afterwards.
                .Select((revision, index) => Prepare(revision, index, verdictOf, firstVerdictBudget, owned))
                .Publish(prepared => Observable.Merge(
                    // Ordered admission: one revision at a time, each forwarded only once its own
                    // FIRST decision admits it AND no refusal has been latched for it (review of
                    // #7016, thread 4336840192). The latch is consulted first: a verdict that
                    // answered true and then false while it was being set up has both latched by
                    // the time the revision gets here, and must be refused, never forwarded ahead of
                    // the follow branch that would have ended the sequence.
                    prepared
                        .Select(item => item.Refused.Merge(item.First).Take(1)
                            .SelectMany(decision => decision.Admitted
                                ? Observable.Return(item)
                                : Observable.Throw<Prepared<T>>(EndOf(decision))))
                        .Concat()
                        .Do(item => admitted.OnNext(item.Index))
                        .Select(item => item.Revision),
                    // Terminal refusal: a revision's verdict turning false at ANY point from its
                    // arrival until a newer revision is admitted ends the sequence — a false while it
                    // waits in the queue included, even if the verdict turns true again before its
                    // turn (thread 4336694621). Its verdict subscription is released when its follow
                    // ends, however it ends.
                    prepared.SelectMany(item => item.Refused
                        .TakeUntil(admitted.Where(n => n > item.Index))
                        .SelectMany(decision => Observable.Throw<T>(EndOf(decision)))
                        .Finally(() => owned.Remove(item.Connection)))));
            owned.Add(pipeline.Subscribe(observer));
            owned.Add(admitted);
            return owned;
        });
    }

    /// <summary>One verdict: admitted, DECIDED refused, or undecided (with its cause, if any).</summary>
    private sealed record Decision(bool Admitted, bool Undecided, Exception? Cause)
    {
        public static readonly Decision Admit = new(true, false, null);
        public static readonly Decision Deny = new(false, false, null);
        public static Decision NoVerdict(Exception? cause) => new(false, true, cause);
    }

    private sealed record Prepared<T>(
        T Revision, long Index, IObservable<Decision> First, IObservable<Decision> Refused,
        System.Reactive.Disposables.CompositeDisposable Connection);

    private static Prepared<T> Prepare<T>(
        T revision, int index, Func<T, IObservable<bool>> verdictOf, TimeSpan firstVerdictBudget,
        System.Reactive.Disposables.CompositeDisposable owned)
    {
        var verdict = Observable.Defer(() => verdictOf(revision))
            .DistinctUntilChanged()
            // Only the FIRST verdict is bounded; after it the verdict may stay quiet for as long as
            // the admission holds.
            .Timeout(Observable.Timer(firstVerdictBudget), _ => Observable.Never<long>())
            .Select(ok => ok ? Decision.Admit : Decision.Deny)
            // A verdict that stalls or faults reached NO verdict: it admits nothing, and says so.
            .Catch((Exception ex) => Observable.Return(Decision.NoVerdict(ex)))
            // A verdict that completes without ever answering admits nothing — undecided too.
            .DefaultIfEmpty(Decision.NoVerdict(null))
            // ONE subscription to the verdict per revision.
            .Publish();
        // Both latches are attached BEFORE the verdict connects, so a verdict that answers during
        // its own subscription cannot slip a value past them.
        var first = verdict.Take(1).Replay(1);
        var refused = verdict.Where(decision => !decision.Admitted).Take(1).Replay(1);
        var connection = new System.Reactive.Disposables.CompositeDisposable();
        // Owned by the outer subscription first: if that is already disposed, this disposes the
        // connection at once and nothing below stays subscribed.
        owned.Add(connection);
        first.ConnectOwnedBy(connection);
        refused.ConnectOwnedBy(connection);
        verdict.ConnectOwnedBy(connection);
        return new Prepared<T>(revision, index, first, refused, connection);
    }
}
