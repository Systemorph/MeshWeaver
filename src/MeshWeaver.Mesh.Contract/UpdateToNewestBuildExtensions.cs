using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Asks a per-node hub to recycle itself IF it serves an older build than the one its NodeType now
/// publishes. The hub decides from its own stale-build offer (<c>StaleBuildBanner</c>,
/// MeshWeaver.Graph), which is the one place that knows: the bound build is instance state,
/// invisible to any caller.
/// </summary>
/// <param name="Reason">Carried on the hub's own <see cref="DisposeRequest"/> (#3510).</param>
public record UpdateToNewestBuildRequest(string Reason) : IRequest<UpdateToNewestBuildResponse>;

/// <summary>Response to <see cref="UpdateToNewestBuildRequest"/>.</summary>
/// <param name="Recycling">True when the hub was stale and is now recycling itself; false when it
/// was already current (or a served-build mismatch, #2471, where a recycle re-binds the same copy),
/// in which case nothing was torn down.</param>
public record UpdateToNewestBuildResponse(bool Recycling);

/// <summary>
/// The portal's one-click <b>Update</b> (maintainer, 2026-11-22): recycle a page's hub ONLY while a
/// newer build is available, decided by the hub itself at the moment of the press.
///
/// <para>🚨 <b>Why the hub decides, and recycles itself.</b> The Update button is offered to every
/// viewer of a page, not only to holders of <c>Update</c>, because in the stale state the recycle
/// writes no data and binds a build the type already published. That argument holds only while the
/// hub IS stale, and only for ONE recycle. Two review findings on MeshWeaver#8458 shaped this:</para>
/// <list type="number">
///   <item>A button rendered from a menu snapshot can outlive the stale state (another viewer's
///     press, the automatic recycle), so the decision cannot be the rendered menu.</item>
///   <item>A "check, then the caller posts the dispose" is not atomic: two overlapping presses both
///     read "stale", and the second dispose lands on the fresh, current activation.</item>
/// </list>
/// <para>So the hub handles the request on its own action block, which is single-threaded: it
/// CLAIMS its offer (sets it to null, which also clears the button and the banner), answers, and
/// posts its own <see cref="DisposeRequest"/>. A second press finds no offer and is answered
/// <c>false</c> without any teardown. The answer is posted BEFORE the dispose, and the caller's
/// follow-up read can only arrive after both, so it queues behind the dispose and is answered by the
/// fresh activation (the read itself re-probes a <c>ShuttingDown</c> NACK, #1726).</para>
///
/// <para>What this does NOT close: a <see cref="DisposeRequest"/> is not permission-checked at its
/// target, so a client that skips this method can still post one. That is a property of the
/// platform's recycle surface, not of this button; see <c>Doc/Architecture/StaleStateUntilRecycle</c>
/// → <i>The Update button</i>.</para>
/// </summary>
public static class UpdateToNewestBuildExtensions
{
    /// <summary>
    /// Asks the hub at <paramref name="path"/> to recycle itself if a newer build is available, and
    /// emits <c>true</c> once the fresh activation answered a read; emits <c>false</c> when the hub
    /// was current, and nothing was torn down. Cold: the request is posted on Subscribe.
    /// </summary>
    /// <param name="hub">The caller's hub, which must outlive the target (see <see cref="HubRecycleExtensions"/>).</param>
    /// <param name="path">The page's node path.</param>
    /// <param name="reason">Carried on the target's <see cref="DisposeRequest"/> (#3510).</param>
    /// <param name="budget">How long to wait for the recycled address to answer again;
    /// <see cref="HubRecycleExtensions.DefaultRecycleBudget"/> when omitted.</param>
    public static IObservable<bool> UpdateToNewestBuild(
        this IMessageHub hub, string path, string reason, TimeSpan? budget = null)
        => Observable.Defer(() => hub.Observe(
                    new UpdateToNewestBuildRequest(reason),
                    o => o.WithTarget(new Address(path))))
            .Take(1)
            .SelectMany(answer => answer.Message.Recycling
                ? hub.GetMeshNode(path, budget ?? HubRecycleExtensions.DefaultRecycleBudget).Take(1).Select(_ => true)
                : Observable.Return(false));
}
