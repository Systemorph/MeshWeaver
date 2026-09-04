namespace MeshWeaver.Messaging;

/// <summary>
/// Why a hosted-hub lookup produced — or did not produce — a hub.
///
/// <para>🚨 <b>Why this exists</b> (Systemorph/MeshWeaver#3243). <c>GetHostedHub</c> answers
/// <c>null</c> for conditions that are not remotely alike, and every caller that only sees the
/// null has to GUESS which one it was. Two of them matter and they belong at opposite log levels:
/// a host that is going down (<see cref="HostShuttingDown"/>) is a teardown race nothing can or
/// should prevent, while a configuration that threw (<see cref="ConstructionFaulted"/>) is a
/// defect somebody must look at. <c>MessageHubGrain</c> reported BOTH at <c>fail:</c> with a
/// sentence that listed the two possibilities and committed to neither — so every pod rollout
/// fingerprinted and ticketed an expected shutdown race. Naming the condition where it is KNOWN
/// beats re-deriving it at the caller, which cannot see the container at all.</para>
///
/// <para>This is the same distinction <c>CancellationClassifier</c> draws for cooperative
/// cancellation (issues #2152 / #2182): the level a call site chooses is a ticketing decision,
/// not a verbosity knob, and downgrading one is never licence to swallow the outcome — the
/// caller is still answered, and answered accurately.</para>
/// </summary>
public enum HostedHubOutcome
{
    /// <summary>
    /// The outcome was not classified — the <see cref="IMessageHub"/> implementation does not
    /// implement <see cref="IMessageHub.TryGetHostedHub"/> and only returned a null hub. Zero so
    /// that <c>default(HostedHubResult)</c> is coherent (no hub, no claim about why). Callers
    /// must treat this as "unknown", never as an expected shutdown.
    /// </summary>
    Unclassified = 0,

    /// <summary>A hub is available — either already registered, or newly constructed.</summary>
    Available,

    /// <summary>
    /// No hub exists at that address and none was requested
    /// (<see cref="HostedHubCreation.Never"/>) — a pure read miss, the routing hot path's
    /// ordinary answer.
    /// </summary>
    Absent,

    /// <summary>
    /// Creation was refused, or could not run, because this host — or an ancestor — is shutting
    /// down. Either the collection's creation freeze had already flipped, or the container the
    /// hub would have been built from is disposed. An EXPECTED teardown race: nothing failed,
    /// nothing was written, and the next access re-activates on a live host.
    /// </summary>
    HostShuttingDown,

    /// <summary>
    /// Hub construction ran and FAULTED — the configuration lambda, a synchronous buildup action,
    /// or the container threw while the host was live. <see cref="HostedHubResult.Error"/>
    /// carries the exception, which <c>HostedHubsCollection</c> has already logged with its
    /// stack. A real defect; stays at fail level.
    /// </summary>
    ConstructionFaulted,
}

/// <summary>
/// The answer to a hosted-hub lookup: the hub when there is one, plus WHY when there is not.
/// </summary>
/// <param name="Hub">The hosted hub, or null when none was produced.</param>
/// <param name="Outcome">Which condition produced this answer.</param>
/// <param name="Error">
/// The exception behind the outcome, when there was one — set for
/// <see cref="HostedHubOutcome.ConstructionFaulted"/>, and for the
/// <see cref="HostedHubOutcome.HostShuttingDown"/> case that surfaced as an
/// <see cref="ObjectDisposedException"/> off a disposed container. Null otherwise. It rides along
/// so a caller's log line can carry the real cause instead of asserting one.
/// </param>
public readonly record struct HostedHubResult(
    IMessageHub? Hub,
    HostedHubOutcome Outcome,
    Exception? Error)
{
    /// <summary>
    /// True when the lookup produced no hub because the host is going down — the one outcome a
    /// caller must NOT report as a fault.
    /// </summary>
    public bool IsShutdownRace => Outcome == HostedHubOutcome.HostShuttingDown;
}
