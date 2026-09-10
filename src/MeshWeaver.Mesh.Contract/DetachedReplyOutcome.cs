using System.Reactive.Linq;

namespace MeshWeaver.Mesh;

/// <summary>
/// The single terminal of a source that a DETACHED REPLY depends on — value, fault, or
/// <b>nothing at all</b>.
///
/// <para>🚨 <b>Why this type exists: a two-arm <c>Subscribe</c> is not total.</b> Rx has THREE
/// terminal states, not two (MeshWeaver#2454). A handler that has already returned
/// <c>Processed()</c> and owes its reply from a detached chain answers the caller from
/// <c>onNext</c> and from <c>onError</c> — and says <b>nothing at all</b> when the source
/// COMPLETES EMPTY. There is no fault to log, no value to post and no stage to record: the
/// delivery trail ends at <c>HANDLER_EXIT state=Processed</c>, and the caller waits out its
/// entire budget for a verdict that is never coming. That is the shape reported in
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/3674">#3674</see> — <i>"a write that
/// is neither applied nor refused"</i> — and in
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/3510">#3510</see> before it.</para>
///
/// <para><b>Totality by construction, not by discipline.</b> Remembering to write a third arm at
/// each site is exactly the discipline that failed: <c>MeshExtensions</c> states the rule twice in
/// its own comments and still left three of its upsert legs on two arms. Composing through
/// <c>DetachedReplyOutcome.Of</c> turns each of the three terminations Rx DELIVERS into exactly one
/// outcome, so the site cannot be silent when its source terminates — and the leg becomes drivable
/// without a mesh, which is what makes the empty-completion case testable at all (a live mesh
/// cannot arrange it on demand — the same reason
/// <c>PresentationScreenExtensions.LastKnownOnFault</c> is observable-in / observable-out).</para>
///
/// <para>🚨 <b>TOTALITY IS NOT LIVENESS, and this type gives only the first.</b> A source that
/// never emits AND never completes — <c>Observable.Never</c>, a request whose response is lost, the
/// starvation shape behind <see href="https://github.com/Systemorph/MeshWeaver/issues/2742">#2742</see>
/// — reaches no arm at all, and <c>Of</c> cannot rescue it: every operator it applies
/// (<c>Take(1)</c>, <c>Select</c>, <c>Catch</c>, <c>DefaultIfEmpty</c>) reacts to a termination and
/// none MANUFACTURES one. Read the guarantee strictly: <i>if the source terminates, exactly one
/// outcome is emitted.</i> The operator that DOES manufacture a termination is <c>Timeout</c> — it
/// is a composition over the source too, and it is the right tool here — so a leg whose source can
/// starve still owes its own, exactly as <c>DispatchInnerCreate</c>
/// (<c>InnerCreateVerdictBound</c>) and the no-op probe (<c>NodeOpForwardTimeout</c>) carry one.
/// Compose <c>Of</c> AROUND that bound and both properties hold; composing through this type alone
/// neither adds nor excuses it. See <c>Doc/Architecture/WriteVerdictTotality</c> → "The FOURTH
/// outcome".</para>
///
/// <para>🚨 <b>Empty is NOT a value.</b> <c>DefaultIfEmpty()</c> on the source itself would emit
/// <c>default(T)</c> — <c>null</c> for a reference type — which the value arm then reads as a real
/// answer (<c>MeshExtensions</c>' upsert read leg records why: an empty read turned into a
/// <c>null</c> would be read as "the node does not exist" and become a CREATE). "Produced nothing"
/// has to be its own outcome, and one the caller is told about.</para>
/// </summary>
/// <typeparam name="T">The source's value type.</typeparam>
/// <param name="HasValue">True when the source emitted; <see cref="Value"/> is then its first emission.</param>
/// <param name="Value">The source's first emission, when <paramref name="HasValue"/>.</param>
/// <param name="Error">The source's fault, when it faulted before emitting.</param>
internal readonly record struct DetachedReplyOutcome<T>(bool HasValue, T? Value, Exception? Error)
{
    /// <summary>
    /// The source produced neither a value nor a fault — it completed empty. The caller MUST be
    /// answered anyway, and the answer is a refusal that says the outcome is unknown, never a
    /// success and never a silence.
    /// </summary>
    internal bool CompletedEmpty => !HasValue && Error is null;
}

/// <summary>
/// Turns any source a detached reply hangs off into a source that emits EXACTLY ONE
/// <see cref="DetachedReplyOutcome{T}"/> and completes. See that type for why.
/// </summary>
internal static class DetachedReplyOutcome
{
    /// <summary>
    /// <c>value → HasValue</c>, <c>fault → Error</c>, <c>completed empty → CompletedEmpty</c> — one
    /// emission for each of the three terminations Rx delivers, so a subscriber cannot fail to
    /// answer a source that TERMINATES. <b>It adds no deadline</b> — none of its operators
    /// manufactures a termination, they only react to one — so a source that never terminates still
    /// reaches nobody. Bounding that is a <c>Timeout</c> the caller composes UNDER this (see the
    /// type's remarks).
    ///
    /// <para><c>Take(1)</c> comes FIRST so the outcome is the source's own first terminal: a
    /// source that emits and then faults has already answered, and its late fault must not
    /// overwrite the answer with a refusal. It also makes the outcome exactly-once for a source
    /// that can emit more than once (the update queue's result subject replays, and
    /// <c>UpdateRemote</c>'s late-verdict chain can re-emit), so a reply can never be posted
    /// twice.</para>
    ///
    /// <para><c>Catch</c> is not swallow-and-continue: it does not resume the sequence, it
    /// converts the fault into the one thing a detached reply can act on — a terminal the caller
    /// is told about. The exception itself is carried through in
    /// <see cref="DetachedReplyOutcome{T}.Error"/> and is the site's to log and report.</para>
    /// </summary>
    /// <typeparam name="T">The source's value type.</typeparam>
    /// <param name="source">The source the detached reply hangs off.</param>
    /// <returns>A source emitting exactly one outcome.</returns>
    internal static IObservable<DetachedReplyOutcome<T>> Of<T>(IObservable<T> source)
        => source
            .Take(1)
            .Select(value => new DetachedReplyOutcome<T>(true, value, null))
            .Catch((Exception ex) => Observable.Return(new DetachedReplyOutcome<T>(false, default, ex)))
            .DefaultIfEmpty(new DetachedReplyOutcome<T>(false, default, null));
}
