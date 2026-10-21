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
/// false (<see cref="RevisionAdmission.AdmitEachRevision{T}"/>).</para>
/// </summary>
/// <param name="owner">The owning hub that serves the subscription.</param>
/// <param name="delivery">The admitted subscribe delivery, with the subscriber's context.</param>
/// <returns>Null when nothing is followed; otherwise how each served revision is decided.</returns>
public delegate ClientReadFollow? ClientSubscriptionReadAdmission(
    IMessageHub owner, IMessageDelivery<SubscribeRequest> delivery);

/// <summary>How a followed client subscription decides each revision it serves.</summary>
/// <param name="VerdictOf">The live verdict for one served revision (the reduced value, e.g. the node itself).</param>
/// <param name="FirstVerdictBudget">How long a revision's first verdict may take; a stalled one is a refusal.</param>
public sealed record ClientReadFollow(Func<object?, IObservable<bool>> VerdictOf, TimeSpan FirstVerdictBudget);

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
/// Couples every forwarded revision to its OWN admission decision (MeshWeaver#4306, follow-up 1).
/// </summary>
public static class RevisionAdmission
{
    /// <summary>
    /// Forwards each revision of <paramref name="revisions"/> only after the verdict decided FOR THAT
    /// REVISION admits it, in order, and ends the sequence with <paramref name="refusal"/> as soon as
    /// any verdict is false.
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
    /// <paramref name="firstVerdictBudget"/>: a stalled verdict is a refusal. A verdict that faults or
    /// completes without an answer is a refusal too (fail closed).</para>
    /// </summary>
    /// <typeparam name="T">The revision type.</typeparam>
    /// <param name="revisions">The revisions to forward.</param>
    /// <param name="verdictOf">The live verdict for one revision; cold, subscribed once per revision.</param>
    /// <param name="firstVerdictBudget">How long a revision's first verdict may take.</param>
    /// <param name="refusal">The error that ends the sequence when a verdict is false.</param>
    /// <returns>The admitted revisions, ending with <paramref name="refusal"/> once one is refused.</returns>
    public static IObservable<T> AdmitEachRevision<T>(
        this IObservable<T> revisions,
        Func<T, IObservable<bool>> verdictOf,
        TimeSpan firstVerdictBudget,
        Func<Exception> refusal)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(verdictOf);
        ArgumentNullException.ThrowIfNull(refusal);
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
                        .Select(item => item.Refused.Select(_ => false).Merge(item.First).Take(1)
                            .SelectMany(ok => ok
                                ? Observable.Return(item)
                                : Observable.Throw<Prepared<T>>(refusal())))
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
                        .SelectMany(_ => Observable.Throw<T>(refusal()))
                        .Finally(() => owned.Remove(item.Connection)))));
            owned.Add(pipeline.Subscribe(observer));
            owned.Add(admitted);
            return owned;
        });
    }

    private sealed record Prepared<T>(
        T Revision, long Index, IObservable<bool> First, IObservable<bool> Refused,
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
            .Catch((Exception _) => Observable.Return(false))
            // A verdict that completes without ever answering admits nothing.
            .DefaultIfEmpty(false)
            // ONE subscription to the verdict per revision.
            .Publish();
        // Both latches are attached BEFORE the verdict connects, so a verdict that answers during
        // its own subscription cannot slip a value past them.
        var first = verdict.Take(1).Replay(1);
        var refused = verdict.Where(ok => !ok).Take(1).Replay(1);
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
