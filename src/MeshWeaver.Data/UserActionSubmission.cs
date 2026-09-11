using System;
using System.Reactive.Linq;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// The ONE way a client submits a person's action — a click, a blur, a dialog dismissal — on a
/// synchronization stream (issue #3986).
///
/// <para>🚨 <b>Why this is not <c>Stream.Hub.Post(…)</c>.</b> A bare <c>Post</c> is
/// fire-and-forget: the sender owes nothing and therefore WAITS for nothing, so its stream is free
/// to be released while the action is still crossing to the owner. When it lands, the owner's
/// per-stream <c>sync/{id}</c> sub-hub — the only place <c>ClickedEvent</c>, <c>BlurEvent</c> and
/// <c>CloseDialogEvent</c> are handled, and the only place the <c>ClickAction</c> closure lives —
/// has already been torn down by the <c>UnsubscribeRequest</c> that release posted, and the action
/// is refused: it did not run and never will (<c>DataExtensions.RefuseStreamMessage</c>,
/// issue #3566). Measured in production 2026-09-10 on memex-cloud
/// (<c>Catalog/Categories/Cat-Insurance</c>): one click accepted by the portal, discarded, and
/// nothing shown to the person who made it.</para>
///
/// <para><b>What this changes, and the one mechanism it uses.</b> The action is posted through
/// <c>Observe</c>, which registers the response callback BEFORE the post. That single fact is the
/// whole fix: a pending response callback is exactly what the hub's own <b>Quiescing</b> phase
/// drains, and the <c>UnsubscribeRequest</c> that releases the owner-side stream is posted from the
/// stream's disposables, which run later still — in <c>ShutDown</c>'s <c>DisposeImpl</c>. So an
/// accepted-but-unacknowledged action ORDERS AHEAD of the release by construction, through the
/// lifecycle the hub already has. No timer, no grace period, no retry, no watchdog, no second
/// disposal gate — the things #3566 rules out and this keeps ruling out. Teardown lets owed work
/// FINISH; it is never delayed and hoped over.</para>
///
/// <para><b>An action that genuinely cannot run is still refused — and now it stops killing the
/// page.</b> The owner answers <see cref="UserActionAccepted"/> only from its stream-scoped
/// handler; a stream that is already gone answers a <see cref="DeliveryFailure"/> carrying
/// <see cref="ErrorType.Rejected"/> and the localized <c>error.userActionNotRun</c> sentence.
/// Because the callback is registered, that refusal is matched to THIS action instead of falling
/// through to the mirror's blanket <c>DeliveryFailure</c> handler, which answers <c>OnError</c> and
/// faults the whole synchronization stream — every view bound to it dying over one lost click. The
/// <c>onRefused</c> callback is where a UI puts the sentence in front of the person.</para>
///
/// <para>See <c>Doc/Architecture/RefusingALostUserAction</c>.</para>
/// </summary>
public static class UserActionSubmission
{
    /// <summary>
    /// Submits <paramref name="action"/> to the stream's owner and holds the stream's release until
    /// the owner has accepted it. Posts immediately — <c>Observe</c> registers the callback and
    /// posts eagerly — and owns its own subscription, so callers subscribe to nothing.
    /// </summary>
    /// <param name="stream">The stream the person acted on.</param>
    /// <param name="action">The click, blur or dialog dismissal.</param>
    /// <param name="actingUser">
    /// The acting person's <see cref="AccessContext"/>, which also decides the language of the
    /// refusal sentence. A user action must carry the CLICKING user's identity: the stream's sync
    /// hub has none of its own, so a context-less post fails closed in <c>PostPipeline</c> and the
    /// action's downstream write is denied. Pass <see langword="null"/> only where no user identity
    /// exists (infrastructure, tests).
    /// </param>
    /// <param name="onRefused">
    /// Receives the already-localized sentence explaining that the action did not run — the stream
    /// was gone, or the owner NACKed. This is the surface that tells the person their click did not
    /// happen; when it is <see langword="null"/> the refusal is logged and nothing else.
    /// </param>
    /// <returns>
    /// The subscription to the receipt. Disposing it cancels the pending callback — and with it the
    /// ordering guarantee — so a UI normally ignores it; it exists so a caller that outlives the
    /// action can release the wait deliberately.
    /// </returns>
    public static IDisposable SubmitUserAction(
        this ISynchronizationStream stream,
        IUserAction action,
        AccessContext? actingUser = null,
        Action<string>? onRefused = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(action);

        // 🚨 PRESENCE (HubIfHeld), not liveness: this refuses in exactly the cases a bare Post
        // would have been dropped anyway, and never one more. A stream that has released its hub
        // has nothing to post FROM — #3321 step 3 made that a state the contract admits — and the
        // honest answer is the same sentence a gone owner-side stream produces.
        if (stream.HubIfHeld() is not { } hub)
        {
            onRefused?.Invoke(LocalizationCatalog.Get(
                "error.userActionNotRun", actingUser?.Locale, action.ActionArea));
            return System.Reactive.Disposables.Disposable.Empty;
        }

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(UserActionSubmission).FullName!);

        return hub.Observe<UserActionAccepted>(
                action,
                o => actingUser is null
                    ? o.WithTarget(stream.Owner)
                    : o.WithTarget(stream.Owner).WithAccessContext(actingUser))
            .Subscribe(
                _ => { },
                ex =>
                {
                    // The owner's own sentence when it produced one (it is resolved off the acting
                    // user's locale at the refusal site); the catalog otherwise. Never a literal.
                    var sentence = (ex as DeliveryFailureException)?.Failure.Message
                        ?? LocalizationCatalog.Get(
                            "error.userActionNotRun", actingUser?.Locale, action.ActionArea);
                    logger?.LogWarning(ex,
                        "User action {Action} on area {Area} of stream {StreamId} was refused by owner {Owner}",
                        action.GetType().Name, action.ActionArea, stream.StreamId, stream.Owner);
                    onRefused?.Invoke(sentence);
                });
    }
}
