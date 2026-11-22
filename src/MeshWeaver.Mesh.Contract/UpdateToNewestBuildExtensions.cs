using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Asks a per-node hub whether it serves an OLDER build than the one its NodeType now publishes.
/// The hub answers from its own stale-build offer (<c>StaleBuildBanner</c>, MeshWeaver.Graph), which
/// is the one place that knows: the bound build is instance state, invisible to any caller.
/// </summary>
public record NewerBuildAvailableRequest : IRequest<NewerBuildAvailableResponse>;

/// <summary>Response to <see cref="NewerBuildAvailableRequest"/>.</summary>
/// <param name="Available">True iff the hub's offer is "a newer build is available" at the moment
/// it answered. False for a hub on its current build, and for a served-build mismatch (#2471), where
/// a recycle re-binds the same copy.</param>
public record NewerBuildAvailableResponse(bool Available);

/// <summary>
/// The portal's one-click <b>Update</b> (maintainer, 2026-11-22): recycle a page's hub ONLY while a
/// newer build is available, decided at the moment of the press by the hub itself.
///
/// <para>🚨 <b>Why the check runs at execution time, on the target.</b> The Update button is offered
/// to every viewer of a page, not only to holders of <c>Update</c>, because in the stale state the
/// recycle writes no data and binds a build the type already published. That argument holds only
/// while the hub IS stale. A button rendered from a menu snapshot can outlive that state: another
/// viewer's press, the automatic convergence recycle, or a direct call can bring the hub onto the
/// current build first, and a recycle of a CURRENT hub interrupts everyone on it and gains nothing.
/// So the press first asks the hub, and posts the <see cref="DisposeRequest"/> only on a
/// <c>true</c> answer. A <c>false</c> answer posts nothing.</para>
///
/// <para>What this does NOT close: a <see cref="DisposeRequest"/> is not permission-checked at its
/// target, so a client that skips this method can still post one. That is a property of the
/// platform's recycle surface, not of this button, and it is stated in
/// <c>Doc/Architecture/StaleStateUntilRecycle</c> → <i>The Update button</i>.</para>
/// </summary>
public static class UpdateToNewestBuildExtensions
{
    /// <summary>
    /// Asks the hub at <paramref name="path"/> whether a newer build is available; if so, recycles
    /// it (<see cref="HubRecycleExtensions.RecycleNode"/>) and emits <c>true</c> once the fresh
    /// activation answered. Emits <c>false</c>, and posts nothing, when the hub is current. Cold: the
    /// question is asked on Subscribe.
    /// </summary>
    /// <param name="hub">The caller's hub, which must outlive the target (see <see cref="HubRecycleExtensions"/>).</param>
    /// <param name="path">The page's node path.</param>
    /// <param name="reason">Carried on the <see cref="DisposeRequest"/> (#3510).</param>
    /// <param name="budget">How long to wait for the recycled address to answer again.</param>
    public static IObservable<bool> UpdateToNewestBuild(
        this IMessageHub hub, string path, string reason, TimeSpan? budget = null)
        => Observable.Defer(() => hub.Observe(
                    new NewerBuildAvailableRequest(),
                    o => o.WithTarget(new Address(path))))
            .Take(1)
            .SelectMany(answer => answer.Message.Available
                ? hub.RecycleNode(path, budget, reason).Take(1).Select(_ => true)
                : Observable.Return(false));
}
