namespace MeshWeaver.Mesh;

/// <summary>
/// A continuation the <c>EventSubscriptionRunner</c> can fire but cannot itself implement — the
/// extension point for effects that live ABOVE the runner in the assembly graph.
///
/// <para><b>Why this exists.</b> The runner lives in <c>MeshWeaver.Graph</c> and handles the
/// continuations whose effects are graph-native (grant a role, add to a group, post a thread message).
/// <see cref="EventContinuationType.PublishSocialPost"/> is not: it belongs to
/// <c>MeshWeaver.Social</c>, which already references <c>MeshWeaver.Graph</c>. Calling it directly
/// would be a reference cycle, and moving the publisher down into Graph would drag LinkedIn's HTTP
/// surface into the graph layer. So the runner resolves handlers from DI and dispatches by
/// <see cref="ContinuationType"/>; the owning module registers its own.</para>
///
/// <para>🚨 <b>Reactive, like everything the runner touches.</b> <see cref="Execute"/> returns a COLD
/// <see cref="IObservable{T}"/> — the effect runs when the runner subscribes, never on call. A
/// handler whose real work is a <c>Task</c>-returning leaf (an HTTP publish, say) bridges it through
/// <c>IIoPool.Invoke</c>; a bare <c>Observable.FromAsync</c> is forbidden repo-wide.</para>
///
/// <para><b>Failure is an error, not an empty sequence.</b> The runner marks a subscription
/// <c>Fired</c> when the continuation emits and <c>Failed</c> (recording the message on
/// <see cref="EventSubscription.LastError"/>) when it throws. A handler that swallowed its own
/// failure and completed empty would leave a subscription that never fires and never explains
/// itself — the exact shape of bug that let scheduled posts sit silently unpublished. Throw.</para>
/// </summary>
public interface IEventContinuationHandler
{
    /// <summary>The continuation this handler implements. One handler per type; the runner takes the
    /// first match, so registering two for the same type is a configuration error, not a fallback
    /// chain.</summary>
    EventContinuationType ContinuationType { get; }

    /// <summary>
    /// Runs the continuation for <paramref name="subscription"/> and emits the node it acted on.
    /// Cold — the effect runs on Subscribe.
    /// </summary>
    /// <param name="subscription">The subscription being fired, carrying whatever the effect needs
    /// (<see cref="EventSubscription.TargetPath"/> and friends).</param>
    /// <param name="subjectId">The subject the trigger identified — the triggering node's id for a
    /// <see cref="EventTriggerType.NodeChange"/>, otherwise <see cref="EventSubscription.SubjectId"/>
    /// (empty when the effect needs no subject, as a timed publish does not).</param>
    IObservable<MeshNode> Execute(EventSubscription subscription, string subjectId);
}
