namespace MeshWeaver.Observability;

/// <summary>
/// One parsed red-log burst, as the watcher saw it — the input to the identity function.
/// </summary>
/// <param name="Category">The .NET log category, e.g. <c>MeshWeaver.Data.MeshDataSource</c>.</param>
/// <param name="EventId">The event id from the console header (<c>Category[0]</c>) — the identity
/// the CODE assigns to a log site, and the most stable discriminator available.</param>
/// <param name="Severity">Error or Critical.</param>
/// <param name="Message">The message as logged, volatile parts intact.</param>
/// <param name="NormalizedMessage">The message with ids, paths, numbers and labelled identifiers masked.</param>
/// <param name="ExceptionType">The exception type name, when the burst carried one.</param>
/// <param name="TopFrame">The top application stack frame, when the burst carried one.</param>
public record LogBurst(
    string Category,
    int EventId,
    LogSeverity Severity,
    string Message,
    string NormalizedMessage,
    string? ExceptionType,
    string? TopFrame);

/// <summary>
/// Decides what makes two red logs <b>the same incident</b> — the one question this subsystem got
/// wrong repeatedly, and the reason it is an extension point rather than a constant.
///
/// <para>Implement it in a <b>Code node</b> under the fingerprint NodeType and the portal picks it
/// up on recompile, so the rule can be tuned against real traffic in seconds instead of a
/// PR → CI → image rebuild → rollout cycle. Between 2026-08-08 and 08-09 that cycle was paid four
/// times, and each round fixed the sample in front of us and missed the next: too coarse (one
/// incident per category), then per-<c>target:</c>, then per-<c>[Plugin]</c>. The rule was never the
/// hard part; the feedback loop was.</para>
///
/// <para>Return a stable, short token. Anything not obviously stable — a guid, a node path, a
/// tenant, a message-specific subject — belongs OUT of the value, or the same defect fans out into
/// one incident per subject and one ticket per incident.</para>
/// </summary>
public interface ILogIncidentIdentity
{
    /// <summary>The incident key for this burst. Equal keys ⇒ the same incident ⇒ one ticket.</summary>
    /// <param name="burst">The parsed burst.</param>
    /// <returns>A stable identity token; it becomes the incident node's id.</returns>
    string Identity(LogBurst burst);
}
