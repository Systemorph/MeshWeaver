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
    /// The single predicate the area subscription hands to
    /// <see cref="AreaStreamRetry.RetryAreaWithBackoff{T}"/>: retry ONLY a transient hub
    /// miss — never a teardown <see cref="ObjectDisposedException"/> (benign) and never a
    /// <see cref="ErrorType.CompilationInProgress"/> NACK (handled immediately by swapping
    /// to the Progress view, not by spinning).
    /// </summary>
    public static bool ShouldRetryArea(Exception? ex)
        => ex is not ObjectDisposedException
           && TryGetCompilationInProgressNodeType(ex) is null
           && IsTransientHubFailure(ex);
}
