using System.Reactive.Subjects;
using MeshWeaver.Messaging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Circuit-scoped bridge that surfaces failed posts ORIGINATING FROM THE PORTAL HUB to the
/// GUI. The portal hub's <see cref="DeliveryFailure"/> handler (wired by
/// <see cref="PortalErrorReporting.WithPortalErrorReporting"/>) pushes the failure text here
/// off the action-block thread; <c>PortalErrorModal</c> subscribes and raises a modal on the
/// renderer. One instance per circuit (registered <c>Scoped</c>) — same lifetime as the
/// <see cref="PortalApplication"/> it belongs to.
/// </summary>
public sealed class PortalErrorSink : IDisposable
{
    private readonly Subject<string> errors = new();

    /// <summary>Hot stream of user-facing error messages; the modal subscribes on the renderer.</summary>
    public IObservable<string> Errors => errors;

    /// <summary>Push a failure message to the GUI. Safe to call from the hub action-block thread.</summary>
    public void Report(string message)
    {
        try { errors.OnNext(message); }
        catch { /* a faulting subscriber must never wedge the hub posting the failure */ }
    }

    /// <summary>Completes the error stream and releases the underlying subject.</summary>
    public void Dispose()
    {
        try { errors.OnCompleted(); } catch { /* ignore */ }
        errors.Dispose();
    }
}

/// <summary>
/// Wires the portal hub to report UN-AWAITED failed posts to a <see cref="PortalErrorSink"/>.
/// Un-awaited failures (<c>stream.Update</c> writes, fire-and-forget posts) return to the
/// portal hub as a <see cref="DeliveryFailure"/> with no callback to consume them, so they
/// would otherwise be silently logged. This handler catches exactly those and surfaces them
/// to the user as a modal. (Awaited <c>hub.Observe</c> failures already surface per-callsite
/// via the response callback's <c>OnError</c>.)
/// <para>
/// Kept as a standalone extension — NOT inlined into <c>DefaultPortalConfig</c> — so the exact
/// failure → sink wiring is exercised on a plain hub in <c>PortalErrorPopupTest</c> without the
/// full portal DI graph (routing/navigation/circuit services).
/// </para>
/// </summary>
public static class PortalErrorReporting
{
    /// <summary>Adds the <see cref="DeliveryFailure"/> → <paramref name="sink"/> handler to a hub config.</summary>
    public static MessageHubConfiguration WithPortalErrorReporting(
        this MessageHubConfiguration config, PortalErrorSink sink)
        => config.WithHandler<DeliveryFailure>((_, delivery) =>
            {
                sink.Report(Describe(delivery.Message));
                return delivery.Processed();
            },
            // AWAITED failures are excluded HERE, not just by documentation: HandleCallbacks runs
            // first in the rule chain and stamps CallbackDispatched when it hands the failure to a
            // live hub.Observe callback — that call site's OnError already deals with it (retry,
            // fallback, message). Reporting it again pops the RAW failure text as a blocking modal
            // even when the caller recovered — the "Access denied … lacks Thread permission on
            // 'Doc'" modal shown while StartThread's user-partition fallback had already re-anchored
            // the thread. The filter REPLACES the default target-address check; a response's target
            // is always this hub, so the stamp is the only gate needed.
            (_, delivery) => !delivery.Properties.ContainsKey(PostOptions.CallbackDispatched));

    /// <summary>The user-facing line for a failed post — the raw failure reason, or a generic fallback.</summary>
    internal static string Describe(DeliveryFailure failure)
        => string.IsNullOrWhiteSpace(failure.Message)
            ? $"{failure.ErrorType}: a request could not be completed."
            : failure.Message!;
}
