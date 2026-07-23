using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Static, dependency-free classification of the errors a layout-area subscription can
/// surface. Factored out of the Blazor <c>NamedAreaView</c> so the wedge-prevention
/// decisions (retry vs. fail-fast vs. swap-to-Progress vs. log-level) are unit-testable
/// without a renderer, a circuit, or a mesh.
///
/// <para>Why this matters: misclassifying a transient "area not yet addressable" miss as
/// permanent leaves the view blank; misclassifying a permanent miss as transient and
/// resubscribing forever is the inexistent-address message storm that wedged the portal
/// (2026-06-14). Each predicate below has a dedicated test pinning the boundary.</para>
/// </summary>
public static class AreaErrorClassifier
{
    /// <summary>
    /// True when the error is a transient hub/network miss that is worth a BOUNDED retry —
    /// the per-node hub is still bootstrapping, the workspace synced query hasn't emitted,
    /// the request timed out, or routing reports the target hub not (yet) found. These
    /// self-heal once the upstream catches up.
    /// </summary>
    public static bool IsTransientHubFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            if (e is OperationCanceledException) return true;
            var msg = e.Message ?? string.Empty;
            // Framework undeliverable / timeout banners reach the GUI wrapped in a
            // DeliveryFailureException, so the typed checks above don't always catch them.
            if (msg.Contains("No response received in hub", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("target hub was not found", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("undeliverable", StringComparison.OrdinalIgnoreCase)) return true;
            // MessageService's shutdown-drop NACK ("Hub X is shutting down … Rejecting now."):
            // the delivery raced the target hub's DisposeRequest (recycle / restart) — retry-
            // worthy, the address typically reactivates on the next probe. Mirrors
            // MeshNodeStreamCache.IsTransientOwnerFailure so the layers agree.
            if (msg.Contains("is shutting down", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the NodeType path of a <see cref="ErrorType.CompilationInProgress"/> NACK
    /// (so the GUI can swap to that NodeType's Progress view at once), or <c>null</c>.
    /// This is NOT retried — it has dedicated, immediate handling.
    /// </summary>
    public static string? TryGetCompilationInProgressNodeType(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DeliveryFailureException dfe
                && dfe.Failure.ErrorType == ErrorType.CompilationInProgress
                && !string.IsNullOrEmpty(dfe.Failure.NodeTypePath))
            {
                return dfe.Failure.NodeTypePath;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the init-failure reason of a target hub whose <c>Initialize</c> gate FAILED to open
    /// (a BuildupAction faulted or hung → the hub entered its FAILED state and now answers every
    /// request with a typed <see cref="DeliveryFailure"/> carrying
    /// <c>"Hub '&lt;addr&gt;' initialization failed: &lt;reason&gt;"</c>), or <c>null</c>.
    ///
    /// <para>This is a TERMINAL-with-reason case — distinct from both
    /// <see cref="IsTransientHubFailure"/> (a not-yet-online hub that self-heals on retry) and
    /// <see cref="TryGetCompilationInProgressNodeType"/> (a transient mid-compile NACK swapped to
    /// the Progress view). A durably-failed hub will NOT recover on resubscribe, so it must NOT be
    /// retried; and its failure carries the ACTUAL reason, so the GUI renders that reason instead of
    /// the generic "did not become addressable after N retries" spinner (which, when the gate never
    /// opened, doesn't even carry an area name). The reason string is authoritative — the same one
    /// <c>MessageHub.EnterInitializationFailedState</c> stamped from <c>InitializationError</c>. See
    /// <c>Doc/Architecture/HubInitializationFailure.md</c> and issue #323.</para>
    /// </summary>
    public static string? TryGetInitializationFailureReason(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DeliveryFailureException dfe)
            {
                // Both init-failure banners the hub emits — the first request's
                // "initialization failed — <reason>" (HandleInitialize's .Catch) and every later
                // request's "initialization failed: <reason>" (EnterInitializationFailedState's
                // refusal rule) — carry this distinctive substring. Match on the typed failure's
                // message, not a bare Exception.Message, so a coincidental string on some other
                // exception can't be mistaken for a hub init failure.
                var msg = dfe.Failure.Message ?? string.Empty;
                if (msg.Contains("initialization failed", StringComparison.OrdinalIgnoreCase))
                    return msg;
            }
        }
        return null;
    }

    /// <summary>
    /// True for outcomes caused by the user's action (access denied, validation rejection,
    /// node not found) rather than an engineering fault — used to pick a Warning (not Error)
    /// log level so dashboards don't page on "user clicked a thing they couldn't do".
    /// </summary>
    public static bool IsExpectedUserActionFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException) return true;
            if (e is DeliveryFailureException)
            {
                var msg = e.Message ?? string.Empty;
                if (msg.Contains("Access denied", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.StartsWith("No node found", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Validation failed", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("Validation error", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("not allowed", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("permission", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the failure is a routing <b>NotFound</b> — the target node/hub no longer exists
    /// (or never did). This is the "the thing you were viewing is gone" case, distinct from the
    /// other <see cref="IsExpectedUserActionFailure"/> outcomes (access denied / validation), which
    /// carry an actionable message worth showing verbatim.
    ///
    /// <para>Why it needs its own predicate: the routing layer's raw diagnostic —
    /// <c>"No node found at 'rbuergi/_Activity/markdown-…'. Closest ancestor is 'rbuergi'
    /// (remainder='…')"</c> — is a FRAMEWORK-INTERNAL string that must never reach an end user. It
    /// surfaced verbatim when an ephemeral per-view interactive-kernel Activity idle-disposed under a
    /// still-open embedded area (there is no push-signal for owner death — only the area's own re-read
    /// discovers the miss). The GUI renders a graceful "view is no longer available" placeholder for
    /// this case instead, while still logging the raw failure for authors. NOT retried (a gone node
    /// won't come back on its own — that is <see cref="IsTransientHubFailure"/>'s "not yet online"
    /// case, which is a DIFFERENT message: "target hub was not found").</para>
    /// </summary>
    public static bool IsNodeGoneNotFound(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is DeliveryFailureException
                && (e.Message ?? string.Empty).StartsWith("No node found", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when a raw <see cref="DeliveryFailure"/> message (NOT wrapped in a
    /// <see cref="DeliveryFailureException"/>) is a routing <b>NotFound</b> for a node/hub that no
    /// longer exists — or has not yet been created. This is the <see cref="IsNodeGoneNotFound"/>
    /// predicate at the raw-message layer, for the portal hub's un-awaited-<c>DeliveryFailure</c>
    /// handler (<c>PortalErrorReporting</c>).
    ///
    /// <para>Why it needs a message-level twin: a fire-and-forget post (a <c>stream.Update</c> write,
    /// a heartbeat/subscribe/unsubscribe on an interactive-kernel area, a run-cell re-post) whose
    /// target has already been reaped returns to the portal hub as a bare <see cref="DeliveryFailure"/>
    /// with NO awaiting callback to consume it. The <c>NamedAreaView</c> control-stream path already
    /// classifies this gracefully (via <see cref="IsNodeGoneNotFound"/>) and renders a "no longer
    /// available" placeholder — but the un-awaited copy escapes to the portal-wide error MODAL, which
    /// reported it VERBATIM ("<c>No node found at 'thomager12/_Activity/markdown-…'. Closest ancestor
    /// is 'thomager12' …</c>") the instant a fresh viewer opened a course lesson whose <c>--render</c>
    /// output areas point at a not-yet-run per-viewer Activity. A routing NotFound for a gone /
    /// not-yet-created node is benign infrastructure churn — it must be SWALLOWED from the modal, never
    /// shown as "Something went wrong". Matched on the typed <see cref="ErrorType.NotFound"/> AND the
    /// "No node found" banner so a genuine engineering failure with the same ErrorType is untouched.</para>
    /// </summary>
    public static bool IsRoutingNotFoundFailure(DeliveryFailure? failure)
        => failure is not null
           && failure.ErrorType == ErrorType.NotFound
           && (failure.Message ?? string.Empty).StartsWith("No node found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The node PATH an access-denied failure names — extracted so the GUI can degrade gracefully
    /// (e.g. redirect a not-yet-enrolled visitor to a course's public paywall) instead of showing a
    /// raw "Access denied" card. Both access-denied banners put the path in the segment right after
    /// <c>permission on '</c>:
    /// <list type="bullet">
    ///   <item>"Access denied: user 'u' lacks Read permission on 'AgenticEngineering'"</item>
    ///   <item>"User 'u' lacks Read permission on 'AgenticEngineering/Module1'"</item>
    /// </list>
    /// Returns <c>null</c> when the error carries no such quoted path. A routing NotFound
    /// ("No node found at '…'") is deliberately NOT matched — it uses a different banner. Pure —
    /// unit-tested without a renderer or a mesh.
    /// </summary>
    public static string? TryGetAccessDeniedPath(Exception? ex)
    {
        const string marker = "permission on '";
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message ?? string.Empty;
            var open = msg.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (open < 0) continue;
            var start = open + marker.Length;
            var end = msg.IndexOf('\'', start);
            if (end > start)
                return msg[start..end];
        }
        return null;
    }

    /// <summary>
    /// Whether redirecting a viewer who was denied <paramref name="deniedPath"/> to the configured
    /// <paramref name="redirectPath"/> ("if no access ⇒ redirect here") is SAFE — i.e. cannot loop.
    /// True only when a target is set AND the denied node is neither the target itself nor a node under
    /// it: redirecting the target (or its subtree) back to the target would bounce forever, so those
    /// fall through to the honest access-denied instead. A leading '/' on the target is ignored. Pure —
    /// unit-tested. (The redirect TARGET itself comes from the node's PartitionAccessPolicy via
    /// <c>hub.GetRedirectOnDenied(path)</c>, not from this classifier.)
    /// </summary>
    public static bool IsSafeRedirect(string? deniedPath, string? redirectPath)
    {
        if (string.IsNullOrEmpty(deniedPath) || string.IsNullOrWhiteSpace(redirectPath))
            return false;
        var target = redirectPath.Trim().TrimStart('/');
        if (target.Length == 0) return false;
        return !string.Equals(deniedPath, target, StringComparison.Ordinal)
            && !deniedPath.StartsWith(target + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The single predicate the area subscription hands to
    /// <see cref="AreaStreamRetry.RetryAreaWithBackoff{T}"/>: retry ONLY a transient hub
    /// miss — never a teardown <see cref="ObjectDisposedException"/> (benign), never a
    /// <see cref="ErrorType.CompilationInProgress"/> NACK (handled immediately by swapping
    /// to the Progress view, not by spinning), and never a hub init failure (durably FAILED —
    /// a resubscribe hits the same failed hub, so the reason is rendered once, not retried).
    /// </summary>
    public static bool ShouldRetryArea(Exception? ex)
        => ex is not ObjectDisposedException
           && TryGetCompilationInProgressNodeType(ex) is null
           && TryGetInitializationFailureReason(ex) is null
           && IsTransientHubFailure(ex);
}
